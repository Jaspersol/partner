using System.Globalization;
using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Storage;

namespace Partner.Core.Sessions;

/// <summary>
/// <see cref="ISessionStore"/> over an <see cref="IAppendOnlySink"/>.
/// <para>
/// Use <see cref="Open"/> for the real thing (creates <c>&lt;directory&gt;/session/events.jsonl</c> and repairs
/// a torn tail), or the constructor to inject a sink (used by tests to prove durability semantics).
/// </para>
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    private readonly IAppendOnlySink _sink;
    private readonly TimeProvider _clock;
    private readonly bool _flushOnAppend;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private const int IndexStride = 128;
    private readonly List<(long Seq, long Offset)> _index = new();

    /// <summary>
    /// Bumped by every mutation of the underlying log. A cached projection is only reusable while this is
    /// unchanged, so cache validity costs one integer rather than a diff against the log.
    /// </summary>
    private long _version;

    /// <summary>Cached projection and the event list it was built from; dropped whenever the log changes.</summary>
    private ProjectionCache? _cache;
    private readonly object _cacheGate = new();

    private sealed class ProjectionCache(List<SessionEvent> events, long version, SessionView view)
    {
        public List<SessionEvent> Events { get; } = events;
        public long Version { get; set; } = version;
        public SessionView View { get; set; } = view;
        public List<SessionEvent> Pending { get; } = new();
        // Legacy logs may contain forward-looking ranges or revisions; those require a full replay.
        public bool CanIncrement { get; set; } = events.All(e =>
            (!SessionRetraction.TryRead(e, out _, out var to) || to <= view.LastSeq)
            && (e.Kind != SessionEventKind.Edit || e.EditedFromSeq <= view.LastSeq));
    }

    /// <summary>Last seq handed out by <see cref="AppendAsync"/> (may be ahead of durability in buffered mode).</summary>
    private long _assignedSeq;

    /// <summary>Last seq known to be on disk.</summary>
    private long _durableSeq;

    private bool _disposed;

    public JsonlSessionStore(IAppendOnlySink sink, TimeProvider? clock = null, bool flushOnAppend = true)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? TimeProvider.System;
        _flushOnAppend = flushOnAppend;
        _assignedSeq = LoadLastSeq();
        _durableSeq = _assignedSeq;
    }

    /// <summary>Opens (or creates) the log at <c>&lt;directory&gt;/session/events.jsonl</c>, repairing a torn tail.</summary>
    /// <returns>The store and how many bytes of torn tail were discarded.</returns>
    public static (JsonlSessionStore Store, long RepairedBytes) Open(
        string directory,
        TimeProvider? clock = null,
        bool flushOnAppend = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(directory, "session", "events.jsonl");
        var sink = new FileAppendOnlySink(path);
        var repaired = sink.RepairTail();
        return (new JsonlSessionStore(sink, clock, flushOnAppend), repaired);
    }

    public long LastSeq => Interlocked.Read(ref _durableSeq);

    public bool Faulted { get; private set; }

    public async Task<long> AppendAsync(SessionEvent sessionEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            return await AppendCoreAsync(sessionEvent, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<long?> AppendIfCurrentAsync(SessionEvent sessionEvent, long expectedLastSeq, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (Interlocked.Read(ref _assignedSeq) != expectedLastSeq) return null;
            return await AppendCoreAsync(sessionEvent, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Append with the write gate already held.</summary>
    private Task<long> AppendCoreAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        ThrowIfFaulted();

        var next = Interlocked.Read(ref _assignedSeq) + 1;
        if (sessionEvent.Seq != 0 && sessionEvent.Seq != next)
        {
            throw new InvalidOperationException(
                $"Seq is assigned by the store: expected {next} but got {sessionEvent.Seq}.");
        }

        sessionEvent = CloseRetractionRange(sessionEvent, next);
        Validate(sessionEvent, next);

        var record = sessionEvent with { Seq = next, At = sessionEvent.At == default ? _clock.GetUtcNow() : sessionEvent.At };
        var durableBefore = _sink.DurableLength;
        var offset = _sink.Length;
        var line = SessionJson.Serialize(record);
        _sink.Append(line);

        if (_flushOnAppend)
        {
            try
            {
                _sink.FlushToDisk();
            }
            catch
            {
                RollBackAndFault(durableBefore);
                throw;
            }

            Interlocked.Exchange(ref _durableSeq, next);
        }
        // A cached incremental view must not expose an append until its durability barrier succeeds.
        // Buffered mode still invalidates immediately and relies on the sink's normal read visibility.
        RecordProjectionAppend(SessionJson.Deserialize(line), include: _flushOnAppend);

        Interlocked.Exchange(ref _assignedSeq, next);
        if (_sink is FileAppendOnlySink && (next - 1) % IndexStride == 0)
        {
            _index.Add((next, offset));
        }
        return Task.FromResult(next);
    }

    /// <summary>
    /// Removes events from the log for real: the lines are gone rather than hidden behind a retraction.
    /// <para>
    /// Survivors are written to a sibling file and swapped in, so a crash leaves either the old log or the new
    /// one. One content-free <see cref="SessionEventKind.Retract"/> is left for the range: it records that
    /// something was removed without recording what, and keeps the seq space monotonic — seqs are never reused
    /// because memory records point back at them (<c>MemoryRecord.SourceEventSeq</c>).
    /// </para>
    /// <para>
    /// Compaction is lossless: its checkpoint hides folded history while leaving the original log intact.
    /// </para>
    /// </summary>
    public async Task<PurgeResult> PurgeAsync(long fromSeq, long toSeq, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        ArgumentOutOfRangeException.ThrowIfLessThan(fromSeq, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toSeq, fromSeq);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();

            // Include accepted buffered records in the rewrite, and publish them only after the barrier.
            if (Interlocked.Read(ref _assignedSeq) != Interlocked.Read(ref _durableSeq))
            {
                var durableBefore = _sink.DurableLength;
                try { _sink.FlushToDisk(); }
                catch { RollBackAndFault(durableBefore); throw; }
                Interlocked.Exchange(ref _durableSeq, Interlocked.Read(ref _assignedSeq));
                InvalidateProjection();
            }

            var survivors = new List<string>();
            var removed = 0;
            var revisions = 0;

            foreach (var line in _sink.ReadLines())
            {
                var parsed = SessionJson.Deserialize(line);

                // A line that cannot be parsed is kept: it is not shown anywhere, and dropping it would destroy
                // something this store does not understand.
                if (parsed is null)
                {
                    survivors.Add(line);
                    continue;
                }

                if (parsed.Seq >= fromSeq && parsed.Seq <= toSeq)
                {
                    removed++;
                    continue;
                }

                // A revision holds a copy of the removed event's text, so it leaves with it.
                if (parsed.Kind == SessionEventKind.Edit
                    && parsed.EditedFromSeq is { } edited
                    && edited >= fromSeq && edited <= toSeq)
                {
                    revisions++;
                    continue;
                }

                // A retraction whose whole range is gone describes nothing any more.
                if (parsed.Kind == SessionEventKind.Retract
                    && SessionRetraction.TryRead(parsed, out var retractedFrom, out var retractedTo)
                    && retractedFrom >= fromSeq && retractedTo <= toSeq)
                {
                    continue;
                }

                survivors.Add(line);
            }

            if (removed == 0)
            {
                return new PurgeResult(0, 0, LastSeq);
            }

            try
            {
                _sink.Rewrite(survivors);
                RebuildIndex();
                InvalidateProjection();
            }
            catch (RewriteRecoveredException) { throw; }
            catch
            {
                Faulted = true;
                InvalidateProjection();
                throw;
            }

            var tombstone = await AppendCoreAsync(
                new SessionEvent
                {
                    Kind = SessionEventKind.Retract,
                    Source = InputSource.Text,
                    Body = $"purge #{fromSeq}..#{toSeq}",
                    Meta = SessionRetraction.Between(fromSeq, toSeq),
                },
                ct).ConfigureAwait(false);

            // The rewrite is durable by construction; the tombstone must be too. Without it a restart would read
            // a lower high-water mark and hand out a seq that a memory record may already point at.
            try
            {
                _sink.FlushToDisk();
            }
            catch
            {
                Faulted = true;
                throw;
            }

            Interlocked.Exchange(ref _durableSeq, Interlocked.Read(ref _assignedSeq));

            return new PurgeResult(removed, revisions, tombstone);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();

            var durableBefore = _sink.DurableLength;
            try
            {
                _sink.FlushToDisk();
            }
            catch
            {
                RollBackAndFault(durableBefore);
                throw;
            }

            Interlocked.Exchange(ref _durableSeq, Interlocked.Read(ref _assignedSeq));

            // Buffered appends become visible only now; drop any projection built before the flush.
            if (!_flushOnAppend)
            {
                InvalidateProjection();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<SessionEvent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in ReadAsync(1, int.MaxValue, ct).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    public IAsyncEnumerable<SessionEvent> ReadAsync(long fromSeq, int take, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromSeq, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        return ReadCoreAsync(fromSeq, take, ct);
    }

    public async Task<SessionView> ProjectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        var version = Interlocked.Read(ref _version);

        List<SessionEvent>? retained = null;
        SessionView? baseView = null;
        List<SessionEvent>? pending = null;
        lock (_cacheGate)
        {
            if (_cache is { } cached && cached.Version == version)
            {
                if (cached.Pending.Count == 0)
                {
                    return cached.View;
                }

                if (cached.CanIncrement && cached.Pending.All(e =>
                    e.Kind is not (SessionEventKind.Edit or SessionEventKind.Retract)
                    && !SessionProjection.HasCompactedRanges(e)))
                {
                    baseView = cached.View;
                    pending = new List<SessionEvent>(cached.Pending);
                }
                else
                {
                    retained = new List<SessionEvent>(cached.Events);
                }
            }
        }

        if (baseView is not null || retained is not null)
        {
            var rebuilt = baseView is not null
                ? SessionProjection.Append(baseView, pending!)
                : SessionProjection.Build(retained!);
            lock (_cacheGate)
            {
                // Publish only if the log has not moved.
                if (_cache is { } cached && cached.Version == version)
                {
                    cached.View = rebuilt;
                    cached.Pending.Clear();
                    if (retained is not null)
                    {
                        cached.CanIncrement = retained.All(e =>
                            (!SessionRetraction.TryRead(e, out _, out var to) || to <= rebuilt.LastSeq)
                            && (e.Kind != SessionEventKind.Edit || e.EditedFromSeq <= rebuilt.LastSeq));
                    }
                }
            }

            return rebuilt;
        }

        var events = new List<SessionEvent>();
        await foreach (var e in ReadAllAsync(ct).ConfigureAwait(false))
        {
            events.Add(e);
        }

        return Cache(events, version);
    }

    /// <summary>Builds the projection and caches it unless the log moved while reading.</summary>
    private SessionView Cache(List<SessionEvent> events, long version)
    {
        var view = SessionProjection.Build(events);

        lock (_cacheGate)
        {
            // Re-read the version: a write that landed mid-read leaves this view short of it, and caching it
            // would make a temporary gap permanent. Returning it is fine — it is a consistent view of the log as
            // of when it was read.
            if (Interlocked.Read(ref _version) != version)
            {
                return view;
            }

            _cache = new ProjectionCache(events, version, view);
        }

        return view;
    }

    /// <summary>Extends the cached projection for one append, or drops it when there is no valid base.</summary>
    private void RecordProjectionAppend(SessionEvent? appended, bool include)
    {
        lock (_cacheGate)
        {
            var previous = Interlocked.Read(ref _version);
            var version = previous + 1;
            Interlocked.Exchange(ref _version, version);

            if (include
                && appended is not null
                && _cache is { } cached
                && cached.Version == previous)
            {
                cached.Events.Add(appended);
                cached.Version = version;
                cached.Pending.Add(appended);
            }
            else
            {
                _cache = null;
            }
        }
    }

    /// <summary>Marks the log as changed so the next projection rebuilds from disk.</summary>
    private void InvalidateProjection()
    {
        lock (_cacheGate)
        {
            Interlocked.Increment(ref _version);
            _cache = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _sink.Dispose();
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private IAsyncEnumerable<SessionEvent> ReadCoreAsync(
        long fromSeq,
        int take,
        CancellationToken ct)
    {
        return ReadIterator();

        async IAsyncEnumerable<SessionEvent> ReadIterator()
        {
            if (take == 0)
            {
                yield break;
            }

            IEnumerator<string>? reader = null;
            bool hasLine;
            long maxVisibleSeq;
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Capture the watermark with the index and open file generation, not before taking the gate.
                maxVisibleSeq = LastSeq;
                IEnumerable<string> lines;
                if (_sink is FileAppendOnlySink file)
                {
                    var low = 0;
                    var high = _index.Count;
                    while (low < high)
                    {
                        var middle = low + (high - low) / 2;
                        if (_index[middle].Seq <= fromSeq) low = middle + 1;
                        else high = middle;
                    }
                    var offset = low == 0 ? 0 : _index[low - 1].Offset;
                    lines = file.ReadLinesFrom(offset).Select(entry => entry.Line);
                }
                else
                {
                    lines = _sink.ReadLines();
                }

                reader = lines.GetEnumerator();
                // Open the file while the offset and log generation are protected by the write gate.
                hasLine = reader.MoveNext();
            }
            catch
            {
                reader?.Dispose();
                throw;
            }
            finally
            {
                _writeGate.Release();
            }

            using (reader)
            {
                var yielded = 0;
                while (hasLine)
                {
                    ct.ThrowIfCancellationRequested();
                    var parsed = SessionJson.Deserialize(reader!.Current);
                    if (parsed is not null && parsed.Seq >= fromSeq && parsed.Seq <= maxVisibleSeq)
                    {
                        yield return parsed;
                        if (++yielded >= take) yield break;
                    }
                    hasLine = reader!.MoveNext();
                }
            }
        }
    }

    private long LoadLastSeq() => RebuildIndex();

    // Derived only from the log: repair and rewrite can discard it without affecting correctness.
    private long RebuildIndex()
    {
        _index.Clear();
        var max = 0L;
        var count = 0;
        if (_sink is FileAppendOnlySink file)
        {
            foreach (var (offset, line) in file.ReadLinesFrom(0))
            {
                var parsed = SessionJson.Deserialize(line);
                if (parsed is null) continue;
                if (count++ % IndexStride == 0) _index.Add((parsed.Seq, offset));
                max = Math.Max(max, parsed.Seq);
            }
        }
        else
        {
            foreach (var line in _sink.ReadLines())
            {
                var parsed = SessionJson.Deserialize(line);
                if (parsed is not null) max = Math.Max(max, parsed.Seq);
            }
        }
        return max;
    }

    /// <summary>
    /// Resolves an open-ended retraction to the range that exists right now. An open range would also hide
    /// every event appended afterwards, so a "retract from here" would silently swallow the next message the
    /// user sends. The persisted event therefore always carries a closed range.
    /// </summary>
    private static SessionEvent CloseRetractionRange(SessionEvent e, long next)
    {
        if (e.Kind != SessionEventKind.Retract || e.Meta is null)
        {
            return e;
        }

        if (e.Meta.TryGetValue(SessionRetraction.ToKey, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return e;
        }

        if (!e.Meta.ContainsKey(SessionRetraction.FromKey))
        {
            return e;
        }

        var closed = new Dictionary<string, string>(e.Meta)
        {
            [SessionRetraction.ToKey] = (next - 1).ToString(CultureInfo.InvariantCulture),
        };

        return e with { Meta = closed };
    }

    private static void Validate(SessionEvent e, long seq)
    {
        if (string.IsNullOrEmpty(e.Body))
        {
            throw new ArgumentException("Body must not be empty.", nameof(e));
        }

        // Newlines in the body are fine: the log is JSON per line, and the serializer escapes them. What must
        // stay single-line is the *record*, which the sink checks where it writes.

        if (e.Kind == SessionEventKind.Edit)
        {
            if (e.EditedFromSeq is null)
            {
                throw new ArgumentException("Edit events must set EditedFromSeq.", nameof(e));
            }

            if (e.EditedFromSeq.Value >= seq || e.EditedFromSeq.Value < 1)
            {
                throw new ArgumentException(
                    $"EditedFromSeq must reference an earlier seq (got {e.EditedFromSeq}, current {seq}).", nameof(e));
            }
        }
        else if (e.EditedFromSeq is not null)
        {
            throw new ArgumentException("Only Edit events may set EditedFromSeq.", nameof(e));
        }

        // A retraction without a parsable range would silently do nothing, which is worse than a refusal.
        if (e.Kind == SessionEventKind.Retract)
        {
            if (!SessionRetraction.TryRead(e, out var from, out _))
            {
                throw new ArgumentException(
                    $"Retract events must set Meta[\"{SessionRetraction.FromKey}\"] to a positive seq, and "
                    + $"Meta[\"{SessionRetraction.ToKey}\"] (when present) must not precede it.",
                    nameof(e));
            }

            if (from >= seq)
            {
                throw new ArgumentException(
                    $"A retraction must reference earlier seqs (got from={from}, current {seq}).", nameof(e));
            }
        }

        // A turn boundary without a reason is not auditable.
        if (e.Kind == SessionEventKind.TurnEnd)
        {
            if (e.Meta is null || !e.Meta.TryGetValue("reason", out var reason) || string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("TurnEnd events must set Meta[\"reason\"].", nameof(e));
            }

            if (reason is not (TurnEndReason.Completed or TurnEndReason.MaxTokens
                or TurnEndReason.Aborted or TurnEndReason.Error))
            {
                throw new ArgumentException(
                    $"Unknown turn end reason '{reason}'.", nameof(e));
            }
        }
    }

    private void RollBackAndFault(long durableBefore)
    {
        try
        {
            if (_sink.Length != durableBefore)
            {
                _sink.TruncateTo(durableBefore);
            }
        }
        catch
        {
            // Best effort: the store is faulted either way.
        }

        // The log changed underneath whatever was cached — a record went in, or came back out.
        InvalidateProjection();

        // Discard seqs that were handed out but never became durable.
        Interlocked.Exchange(ref _assignedSeq, Interlocked.Read(ref _durableSeq));
        Faulted = true;
    }

    private void ThrowIfFaulted()
    {
        if (Faulted)
        {
            throw new InvalidOperationException(
                "Session store is faulted after a storage failure; the log state is unknown.");
        }
    }
}

/// <summary>
/// The range a <see cref="SessionEventKind.Retract"/> event removes. Parsing lives here so the store's
/// validation and the projection's filtering cannot disagree about what a malformed range means.
/// </summary>
public static class SessionRetraction
{
    public const string FromKey = "fromSeq";
    public const string ToKey = "toSeq";

    /// <summary>Reads the range; false when the event is not a well-formed retraction.</summary>
    public static bool TryRead(SessionEvent e, out long from, out long to)
    {
        ArgumentNullException.ThrowIfNull(e);

        from = 0;
        to = long.MaxValue;

        if (e.Kind != SessionEventKind.Retract || e.Meta is null)
        {
            return false;
        }

        if (!e.Meta.TryGetValue(FromKey, out var fromText)
            || !long.TryParse(fromText, CultureInfo.InvariantCulture, out from)
            || from < 1)
        {
            return false;
        }

        if (e.Meta.TryGetValue(ToKey, out var toText) && !string.IsNullOrWhiteSpace(toText))
        {
            if (!long.TryParse(toText, CultureInfo.InvariantCulture, out to) || to < from)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A retraction covering exactly one seq.</summary>
    public static IReadOnlyDictionary<string, string> Single(long seq) => new Dictionary<string, string>
    {
        [FromKey] = seq.ToString(CultureInfo.InvariantCulture),
        [ToKey] = seq.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A retraction covering <paramref name="fromSeq"/>..<paramref name="toSeq"/> inclusive.</summary>
    public static IReadOnlyDictionary<string, string> Between(long fromSeq, long toSeq) => new Dictionary<string, string>
    {
        [FromKey] = fromSeq.ToString(CultureInfo.InvariantCulture),
        [ToKey] = toSeq.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A retraction covering everything from <paramref name="fromSeq"/> onward.</summary>
    public static IReadOnlyDictionary<string, string> From(long fromSeq) => new Dictionary<string, string>
    {
        [FromKey] = fromSeq.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Immutable chunks preserve published views without copying all historical items on append.</summary>
internal sealed class SessionViewItems : IReadOnlyList<SessionViewItem>
{
    private const int ChunkSize = 1024;
    private readonly SessionViewItem[][] _chunks;
    public int Count { get; }

    private SessionViewItems(SessionViewItem[][] chunks, int count)
    {
        _chunks = chunks;
        Count = count;
    }

    public static SessionViewItems From(IReadOnlyList<SessionViewItem> items)
    {
        var chunks = new SessionViewItem[(items.Count + ChunkSize - 1) / ChunkSize][];
        for (var i = 0; i < chunks.Length; i++)
        {
            var length = Math.Min(ChunkSize, items.Count - i * ChunkSize);
            var chunk = new SessionViewItem[length];
            for (var j = 0; j < length; j++) chunk[j] = items[i * ChunkSize + j];
            chunks[i] = chunk;
        }
        return new SessionViewItems(chunks, items.Count);
    }

    public SessionViewItems Append(IReadOnlyList<SessionViewItem> items)
    {
        if (items.Count == 0) return this;
        var total = checked(Count + items.Count);
        var chunks = new SessionViewItem[(total + ChunkSize - 1) / ChunkSize][];
        var full = Count / ChunkSize;
        Array.Copy(_chunks, chunks, full);
        var oldPartial = Count % ChunkSize;
        var consumed = 0;
        for (var i = full; i < chunks.Length; i++)
        {
            var length = Math.Min(ChunkSize, total - i * ChunkSize);
            var chunk = new SessionViewItem[length];
            var existing = i == full ? oldPartial : 0;
            if (existing != 0) Array.Copy(_chunks[i], chunk, existing);
            var added = length - existing;
            for (var j = 0; j < added; j++) chunk[existing + j] = items[consumed++];
            chunks[i] = chunk;
        }
        return new SessionViewItems(chunks, total);
    }

    public SessionViewItem this[int index] => index >= 0 && index < Count
        ? _chunks[index / ChunkSize][index % ChunkSize]
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<SessionViewItem> GetEnumerator()
    {
        foreach (var chunk in _chunks)
            foreach (var item in chunk)
                yield return item;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Builds the projected view from raw log events.</summary>
public static class SessionProjection
{
    internal static SessionView Append(SessionView view, IReadOnlyList<SessionEvent> events)
    {
        var additions = new List<SessionViewItem>();
        var lastSeq = view.LastSeq;
        foreach (var e in events)
        {
            lastSeq = Math.Max(lastSeq, e.Seq);
            if (e.Kind.IsContent() || e.Kind.IsToolControl())
            {
                additions.Add(new SessionViewItem(e.Seq, e.At, e.Kind, e.Source, e.Body, null, 0, e.Meta));
            }
        }
        var items = view.Items as SessionViewItems ?? SessionViewItems.From(view.Items);
        return new SessionView(lastSeq, items.Append(additions));
    }

    public static SessionView Build(IReadOnlyList<SessionEvent> events) => BuildCore(events, false);

    /// <summary>Projects original history while still applying explicit retractions and edits.</summary>
    public static SessionView BuildHistory(IReadOnlyList<SessionEvent> events) => BuildCore(events, true);

    internal static bool HasCompactedRanges(SessionEvent e) =>
        e.Kind == SessionEventKind.Summary && e.Meta?.ContainsKey("compactedRanges") == true;

    private static IEnumerable<(long From, long To)> CompactedRanges(SessionEvent e)
    {
        if (!HasCompactedRanges(e)) return Array.Empty<(long, long)>();
        try
        {
            var ranges = JsonSerializer.Deserialize<long[][]>(e.Meta!["compactedRanges"]);
            return ranges?.Where(r => r is { Length: 2 } && r[0] > 0 && r[1] >= r[0] && r[1] < e.Seq)
                .Select(r => (r[0], r[1])).ToArray() ?? Array.Empty<(long, long)>();
        }
        catch (JsonException)
        {
            return Array.Empty<(long, long)>();
        }
    }

    private static bool IsLegacyCompaction(SessionEvent e, long from, long to, IReadOnlyDictionary<long, SessionEvent> summaries)
    {
        var prefix = $"compacted #{from}..{to} into #";
        if (!e.Body.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(e.Body.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var summarySeq)
            || summarySeq >= e.Seq || !summaries.TryGetValue(summarySeq, out var summary)
            || summary.Meta is null
            || !summary.Meta.TryGetValue("coveredFrom", out var first)
            || !summary.Meta.TryGetValue("coveredTo", out var last)
            || !long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var coveredFrom)
            || !long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var coveredTo)) return false;
        return from >= coveredFrom && to <= coveredTo;
    }

    private static SessionView BuildCore(IReadOnlyList<SessionEvent> events, bool history)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.OrderBy(e => e.Seq).ToList();
        var effective = new Dictionary<long, string>();
        var revisions = new Dictionary<long, int>();
        var summaries = history
            ? ordered.Where(e => e.Kind == SessionEventKind.Summary).ToDictionary(e => e.Seq)
            : null;
        var retractions = new List<(long From, long To)>();
        var lastSeq = 0L;

        // Pass 1: collect revisions (last edit wins) and retractions so the view can be built in seq order.
        foreach (var e in ordered)
        {
            lastSeq = Math.Max(lastSeq, e.Seq);

            if (!history && HasCompactedRanges(e))
            {
                retractions.AddRange(CompactedRanges(e));
            }

            if (SessionRetraction.TryRead(e, out var from, out var to))
            {
                if (!history || !IsLegacyCompaction(e, from, to, summaries!)) retractions.Add((from, to));
                continue;
            }

            if (e.Kind != SessionEventKind.Edit)
            {
                continue;
            }

            var target = e.EditedFromSeq!.Value;
            effective[target] = e.Body;
            revisions[target] = revisions.TryGetValue(target, out var n) ? n + 1 : 1;
        }

        // Pass 2: emit renderable entries — content plus the tool-call pair, which are shown collapsed and so
        // are not content: prompts and compaction ignore them, the session view does not. Revisions, retractions
        // and turn/step markers stay in the raw log only.
        var items = new List<SessionViewItem>();
        foreach (var e in ordered)
        {
            if (e.Kind == SessionEventKind.Edit
                || e.Kind == SessionEventKind.Retract
                || (!e.Kind.IsContent() && !e.Kind.IsToolControl()))
            {
                continue;
            }

            if (retractions.Any(range => e.Seq >= range.From && e.Seq <= range.To))
            {
                continue;
            }

            var edited = effective.TryGetValue(e.Seq, out var revisedBody);
            items.Add(new SessionViewItem(
                e.Seq,
                e.At,
                e.Kind,
                e.Source,
                edited ? revisedBody! : e.Body,
                edited ? e.Body : null,
                revisions.TryGetValue(e.Seq, out var count) ? count : 0,
                e.Meta));
        }

        return new SessionView(lastSeq, SessionViewItems.From(items));
    }
}
