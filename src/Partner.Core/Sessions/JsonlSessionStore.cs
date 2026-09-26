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

            var next = Interlocked.Read(ref _assignedSeq) + 1;
            if (sessionEvent.Seq != 0 && sessionEvent.Seq != next)
            {
                throw new InvalidOperationException(
                    $"Seq is assigned by the store: expected {next} but got {sessionEvent.Seq}.");
            }

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
            return next;
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

    private static void Validate(SessionEvent e, long seq)
    {
        if (string.IsNullOrEmpty(e.Body))
        {
            throw new ArgumentException("Body must not be empty.", nameof(e));
        }

        if (e.Body.Contains('\n') || e.Body.Contains('\r'))
        {
            throw new ArgumentException("Body must be single-line (the log is line-framed).", nameof(e));
        }

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

/// <summary>Builds the projected view from raw log events.</summary>
public static class SessionProjection
{
    public static SessionView Build(IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.OrderBy(e => e.Seq).ToList();
        var effective = new Dictionary<long, string>();
        var revisions = new Dictionary<long, int>();
        var lastSeq = 0L;

        // Pass 1: collect revisions (last edit wins) so the view can be built in seq order.
        foreach (var e in ordered)
        {
            lastSeq = Math.Max(lastSeq, e.Seq);
            if (e.Kind != SessionEventKind.Edit)
            {
                continue;
            }

            var target = e.EditedFromSeq!.Value;
            effective[target] = e.Body;
            revisions[target] = revisions.TryGetValue(target, out var n) ? n + 1 : 1;
        }

        // Pass 2: emit content entries. Revisions and turn/step control markers are not content —
        // they stay in the raw log for auditability but never render as conversation.
        var items = new List<SessionViewItem>();
        foreach (var e in ordered)
        {
            if (e.Kind == SessionEventKind.Edit || !e.Kind.IsContent())
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
