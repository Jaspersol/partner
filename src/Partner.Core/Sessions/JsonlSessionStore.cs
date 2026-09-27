using System.Globalization;
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
        _sink.Append(SessionJson.Serialize(record));

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

        Interlocked.Exchange(ref _assignedSeq, next);
        return Task.FromResult(next);
    }

    /// <summary>
    /// Removes events from the log for real: the lines are gone rather than hidden behind a retraction.
    /// <para>
    /// Survivors are written to a sibling file and swapped in, so a crash leaves either the old log or the new
    /// one. One content-free <see cref="SessionEventKind.Retract"/> is left for the range: it records that
    /// something was removed without recording what, and it keeps the seq space monotonic — seqs are never
    /// reused, because memory records point back at them (<c>MemoryRecord.SourceEventSeq</c>).
    /// </para>
    /// <para>
    /// Only an explicit removal goes through here. Compaction writes a retraction too, but it is lossless by
    /// design: the folded history stays in the log behind the summary.
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

            _sink.Rewrite(survivors);

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
        var events = new List<SessionEvent>();
        await foreach (var e in ReadAllAsync(ct).ConfigureAwait(false))
        {
            events.Add(e);
        }

        return SessionProjection.Build(events);
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

            // Read once, yield in order. The log is line-framed and flushed per record.
            var lines = _sink.ReadLines();
            var yielded = 0;
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();

                var parsed = SessionJson.Deserialize(line);
                if (parsed is null || parsed.Seq < fromSeq)
                {
                    continue;
                }

                yield return parsed;

                if (++yielded >= take)
                {
                    yield break;
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private long LoadLastSeq()
    {
        var max = 0L;
        foreach (var line in _sink.ReadLines())
        {
            var parsed = SessionJson.Deserialize(line);
            if (parsed is not null && parsed.Seq > max)
            {
                max = parsed.Seq;
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

        // A turn boundary without a reason is not auditable (design doc §2.9.1 rule 2).
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

        // Discard seqs that were handed out but never became durable.
        Interlocked.Exchange(ref _assignedSeq, Interlocked.Read(ref _durableSeq));
        Faulted = true;
    }

    private void ThrowIfFaulted()
    {
        if (Faulted)
        {
            throw new InvalidOperationException(
                "Session store is faulted after a failed durability barrier; the log state is unknown.");
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

/// <summary>Builds the projected view from raw log events.</summary>
public static class SessionProjection
{
    public static SessionView Build(IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.OrderBy(e => e.Seq).ToList();
        var effective = new Dictionary<long, string>();
        var revisions = new Dictionary<long, int>();
        var retractions = new List<(long From, long To)>();
        var lastSeq = 0L;

        // Pass 1: collect revisions (last edit wins) and retractions so the view can be built in seq order.
        foreach (var e in ordered)
        {
            lastSeq = Math.Max(lastSeq, e.Seq);

            if (SessionRetraction.TryRead(e, out var from, out var to))
            {
                retractions.Add((from, to));
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

        // Pass 2: emit renderable entries. Content plus the tool-call pair — those are shown collapsed, which
        // is why they are not content: prompts and compaction ignore them, the session view does not.
        // Revisions, retractions and turn/step markers stay in the raw log only.
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
                revisions.TryGetValue(e.Seq, out var count) ? count : 0));
        }

        return new SessionView(lastSeq, items);
    }
}
