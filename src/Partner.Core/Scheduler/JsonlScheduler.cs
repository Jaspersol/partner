using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Storage;

namespace Partner.Core.Scheduler;

public sealed record SchedulerOptions
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>
    /// Where the wall-clock zone comes from when it can change while the process runs (the machine travels, or the
    /// user changes it). Re-read at most once a minute; when it differs, cron tasks are recomputed in the new zone.
    /// Null keeps <see cref="TimeZone"/> for good.
    /// </summary>
    public Func<TimeZoneInfo>? TimeZoneSource { get; init; }

    /// <summary>Consecutive failures after which a task is flagged <see cref="ScheduleTaskStatus.Failed"/> (it stays schedulable).</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Wait when nothing is pending.</summary>
    public TimeSpan IdleWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on a single wait. Purely defensive: it caps the damage of a missed wake-up, it is not
    /// a poll interval (a state change wakes the driver immediately via <see cref="IScheduler.RequestDriveAsync"/>).
    /// </summary>
    public TimeSpan MaxTimerDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Floor on the wait so a perpetually-due (failing) task cannot spin the driver.</summary>
    public TimeSpan MinWait { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Wait before the first retry of a failed delivery; each further failure doubles it.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap on the retry backoff, so a failing task is still retried but never hammers the queue or the disk.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one delivery may wait for the input queue to take an item before it counts as a failed attempt.</summary>
    public TimeSpan EnqueueTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most tasks the scheduler keeps; 0 means no limit. Enforced under the scheduler's own lock, because a caller
    /// that counts first and schedules afterwards lets two concurrent creations both pass.
    /// </summary>
    public int MaxTasks { get; init; }

    /// <summary>The task log is rewritten to one line per task once it has grown past this many lines (and four per task).</summary>
    public int CompactTaskLogAbove { get; init; } = 200;

    /// <summary>
    /// How long a delivery receipt is kept. A receipt only has to outlive the chance of the same occurrence being
    /// delivered again, which ends when the task has moved on; a month is far past that.
    /// </summary>
    public TimeSpan ReceiptRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How late an occurrence may be before <see cref="CatchUpPolicy.Skip"/> treats it as missed.</summary>
    public TimeSpan MissedGrace { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The most missed occurrences <see cref="CatchUpPolicy.All"/> delivers; older ones are dropped.</summary>
    public int CatchUpAllCap { get; init; } = 5;
}

/// <summary>
/// Durable scheduler over two append-only logs.
/// <para>
/// State lives in <c>&lt;directory&gt;/scheduler/tasks.jsonl</c> (one full task snapshot per change, last wins)
/// and <c>&lt;directory&gt;/scheduler/receipts.jsonl</c> (one record per delivered occurrence). The due set is
/// always filtered from the replayed state, so the timer only decides <i>when to look</i>, never <i>what is due</i>.
/// </para>
/// </summary>
public sealed class JsonlScheduler : IScheduler
{
    /// <summary>Status text of the record that deletes a task. Not a <see cref="ScheduleTaskStatus"/> on purpose.</summary>
    private const string TombstoneStatus = "Removed";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAppendOnlySink _tasks;
    private readonly IAppendOnlySink _receipts;
    private readonly IInputQueue _queue;
    private readonly TimeProvider _clock;
    private readonly SchedulerOptions _options;
    private readonly Dictionary<string, ScheduleTask> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receiptKeys = new(StringComparer.Ordinal);

    /// <summary>Earliest next attempt per failing task. In memory on purpose: a restart simply retries once at once.</summary>
    private readonly Dictionary<string, DateTimeOffset> _retryAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _deliverGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTimeOffset? _nextDueAt;
    private bool _disposed;

    public JsonlScheduler(
        string directory,
        IInputQueue queue,
        TimeProvider? clock = null,
        SchedulerOptions? options = null)
        : this(OpenSinks(directory), queue, clock, options)
    {
    }

    private JsonlScheduler(
        (IAppendOnlySink Tasks, IAppendOnlySink Receipts) sinks,
        IInputQueue queue,
        TimeProvider? clock,
        SchedulerOptions? options)
        : this(sinks.Tasks, sinks.Receipts, queue, clock, options)
    {
    }

    /// <summary>Injection point for the two logs — used by tests to prove the durability ordering.</summary>
    public JsonlScheduler(
        IAppendOnlySink tasks,
        IAppendOnlySink receipts,
        IInputQueue queue,
        TimeProvider? clock = null,
        SchedulerOptions? options = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new SchedulerOptions();

        _tasks.RepairTail();
        _receipts.RepairTail();
        Replay();
        CompactLogs(_clock.GetUtcNow());
        RecomputeNextDue();
    }

    private static (IAppendOnlySink Tasks, IAppendOnlySink Receipts) OpenSinks(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.Combine(directory, "scheduler");
        return (
            new FileAppendOnlySink(Path.Combine(root, "tasks.jsonl")),
            new FileAppendOnlySink(Path.Combine(root, "receipts.jsonl")));
    }

    public DateTimeOffset? NextDueAt => _nextDueAt;

    public async Task<ScheduleTask> ScheduleAsync(ScheduleTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(task.TaskId))
        {
            throw new ArgumentException("TaskId must not be empty.", nameof(task));
        }

        if (string.IsNullOrWhiteSpace(task.Payload))
        {
            throw new ArgumentException("Payload must not be empty.", nameof(task));
        }

        ScheduleTask result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_options.MaxTasks > 0 && !_byId.ContainsKey(task.TaskId) && _byId.Count >= _options.MaxTasks)
            {
                throw new InvalidOperationException($"定时任务最多 {_options.MaxTasks} 个，先删掉不用的");
            }

            var stored = task;
            if (task.NextScheduledAt == default)
            {
                var next = task.Trigger.NextAfter(_clock.GetUtcNow(), Zone)
                    ?? throw new InvalidOperationException(
                        $"Trigger '{task.Trigger.Describe()}' has no occurrence in the search horizon.");

                stored = task with { NextScheduledAt = next };
            }

            await PersistTaskAsync(stored, ct).ConfigureAwait(false);
            RecomputeNextDue();
            result = stored;
        }
        finally
        {
            _gate.Release();
        }

        await RequestDriveAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<ScheduleTask> SetStatusAsync(string taskId, ScheduleTaskStatus status, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ThrowIfDisposed();

        ScheduleTask result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_byId.TryGetValue(taskId, out var task))
            {
                throw new InvalidOperationException($"Unknown task '{taskId}'.");
            }

            var updated = task with { Status = status };
            _retryAt.Remove(taskId);

            // Resuming a paused task: the occurrences that passed while it was paused are skipped rather than
            // fired as one late burst (the same rule a restart follows). A Failed task is different — it keeps
            // its obligation, so resuming it retries the delivery it owes, with a fresh attempt count.
            if (status == ScheduleTaskStatus.Active && task.Status == ScheduleTaskStatus.Inactive)
            {
                var now = _clock.GetUtcNow();
                if (task.NextScheduledAt < now)
                {
                    var next = task.Trigger.NextAfter(now, Zone);
                    updated = next is null ? task : updated with { NextScheduledAt = next.Value, Attempt = 0, LastError = null };
                }
            }
            else if (status == ScheduleTaskStatus.Active)
            {
                updated = updated with { Attempt = 0, LastError = null };
            }

            await PersistTaskAsync(updated, ct).ConfigureAwait(false);
            RecomputeNextDue();
            result = updated;
        }
        finally
        {
            _gate.Release();
        }

        await RequestDriveAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> RemoveAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_byId.ContainsKey(taskId))
            {
                return false;
            }

            // A tombstone, not a rewrite: the log stays append-only, and replay drops the task when it meets it.
            ct.ThrowIfCancellationRequested();
            _tasks.Append(JsonSerializer.Serialize(TaskRecord.Tombstone(taskId), Json));
            _tasks.FlushToDisk();
            _byId.Remove(taskId);
            _retryAt.Remove(taskId);
            RecomputeNextDue();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduleTask>> ListAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // Under the gate: the dictionary is mutated by delivery and by the HTTP-driven writes, and enumerating it
        // while one of them runs throws.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _byId.Values
                .OrderBy(t => t.NextScheduledAt)
                .ThenBy(t => t.TaskId, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordRunAsync(string taskId, bool ok, string? error, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byId.TryGetValue(taskId, out var task))
            {
                // Only the run fields move: this is a note about a finished turn, not a change to the schedule.
                await PersistTaskAsync(
                    task with { LastRunAt = at, LastRunOk = ok, LastRunError = ok ? null : error },
                    ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeliveryOutcome> DeliverDueAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // One pass at a time, but not under _gate: the gate is what ListAsync, ScheduleAsync and the HTTP
        // endpoints wait on, and a pass that sits inside it while the input queue is full (EnqueueAsync waits for
        // a free slot) would freeze every one of them behind a queue that is merely busy.
        await _deliverGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset now;
            List<ScheduleTask> due;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                now = _clock.GetUtcNow();
                await RefreshZoneAsync(now, ct).ConfigureAwait(false);

                // The due set comes from persisted state, not from the timer.
                due = _byId.Values
                    .Where(t => t.Status != ScheduleTaskStatus.Inactive && EffectiveDueAt(t) <= now)
                    .OrderBy(t => t.NextScheduledAt)
                    .ThenBy(t => t.TaskId, StringComparer.Ordinal)
                    .ToList();
            }
            finally
            {
                _gate.Release();
            }

            var delivered = 0;
            var failed = 0;
            var skipped = 0;

            foreach (var task in due)
            {
                ct.ThrowIfCancellationRequested();

                var occurrence = task.NextScheduledAt;
                var key = ReceiptKey(task.TaskId, occurrence);

                bool alreadyReceipted;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    alreadyReceipted = _receiptKeys.Contains(key);
                    if (alreadyReceipted)
                    {
                        // Crash landed between the receipt and the state advance: finish the advance only.
                        skipped++;
                        await AdvanceAsync(task, occurrence, now, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (alreadyReceipted)
                {
                    continue;
                }

                if (task.CatchUp == CatchUpPolicy.Skip && now - occurrence > _options.MissedGrace)
                {
                    // Too late to be useful, and the task asked not to be run late: say so instead of firing it.
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_byId.TryGetValue(task.TaskId, out var late) && late.NextScheduledAt == occurrence)
                        {
                            skipped++;
                            await AdvanceAsync(
                                late with { MissedCount = late.MissedCount + 1, LastMissedAt = occurrence },
                                occurrence,
                                now,
                                ct,
                                delivered: false).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    continue;
                }

                if (task.CatchUp == CatchUpPolicy.All && await DropBeyondCapAsync(task, occurrence, now, ct).ConfigureAwait(false))
                {
                    // The oldest occurrences were dropped to honour the cap; the task is due again at the first kept one.
                    skipped++;
                    continue;
                }

                try
                {
                    var itemId = ItemId(task.TaskId, occurrence);

                    // Outside the gate and bounded: a full queue means "try again later" (with backoff), not
                    // "hold everything else until there is room".
                    using (var enqueue = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        enqueue.CancelAfter(_options.EnqueueTimeout);

                        try
                        {
                            await _queue.EnqueueAsync(new InputItem
                            {
                                ItemId = itemId,
                                Source = InputSource.Schedule,
                                Payload = task.Payload,
                                IdempotencyKey = $"schedule:{task.TaskId}:{occurrence.UtcDateTime:O}",
                            }, enqueue.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"input queue did not accept the item within {_options.EnqueueTimeout.TotalSeconds:0.#}s");
                        }
                    }

                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // The task may have been removed, paused or rescheduled while the gate was free. The item
                        // is already in the queue under its idempotency key, so the only thing left is not to
                        // write bookkeeping for a state that no longer exists.
                        if (!_byId.TryGetValue(task.TaskId, out var current)
                            || current.NextScheduledAt != occurrence
                            || current.Status == ScheduleTaskStatus.Inactive)
                        {
                            skipped++;
                            continue;
                        }

                        // Receipt is committed BEFORE the state advance; if this throws the task stays due.
                        await AppendReceiptAsync(new DeliveryReceipt
                        {
                            TaskId = task.TaskId,
                            OccurrenceAt = occurrence,
                            DeliveredAt = now,
                            ItemId = itemId,
                            Attempt = current.Attempt,
                        }, ct).ConfigureAwait(false);

                        _receiptKeys.Add(key);
                        await AdvanceAsync(current, occurrence, now, ct).ConfigureAwait(false);
                        delivered++;
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;

                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_byId.TryGetValue(task.TaskId, out var current) && current.NextScheduledAt == occurrence)
                        {
                            await RecordFailureAsync(current, ex, ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RecomputeNextDue();
                if (_appendsSinceCompact >= CompactCheckEvery)
                {
                    CompactLogs(_clock.GetUtcNow());
                }

                return new DeliveryOutcome(delivered, failed, skipped, _nextDueAt);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _deliverGate.Release();
        }
    }

    public Task RequestDriveAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        // Wake the driver early so a newly scheduled task does not wait for the current timer.
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Lost race with the driver: the timer chunk bounds the delay anyway.
            }
        }

        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DeliverDueAsync(ct).ConfigureAwait(false);
                await _wake.WaitAsync(ComputeWait(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tasks.Dispose();
        _receipts.Dispose();
        _gate.Dispose();
        _deliverGate.Dispose();
        _wake.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private TimeSpan ComputeWait()
    {
        var wait = _nextDueAt is null
            ? _options.IdleWait
            : _nextDueAt.Value - _clock.GetUtcNow();

        if (wait < _options.MinWait)
        {
            wait = _options.MinWait;
        }

        if (wait > _options.MaxTimerDelay)
        {
            wait = _options.MaxTimerDelay;
        }

        return wait;
    }

    private async Task AdvanceAsync(
        ScheduleTask task,
        DateTimeOffset occurrence,
        DateTimeOffset now,
        CancellationToken ct,
        bool delivered = true)
    {
        _retryAt.Remove(task.TaskId);
        var next = task.Trigger.NextAfter(occurrence, Zone);

        // Occurrences missed while the process was down are skipped, never replayed as a burst — unless the task
        // asked for them: All keeps the newest few and delivers them one pass at a time.
        var missed = new List<DateTimeOffset>();
        while (next is not null && next <= now)
        {
            missed.Add(next.Value);
            next = task.Trigger.NextAfter(next.Value, Zone);
        }

        if (task.CatchUp == CatchUpPolicy.All && missed.Count > 0)
        {
            var kept = missed.Count > _options.CatchUpAllCap ? missed.GetRange(missed.Count - _options.CatchUpAllCap, _options.CatchUpAllCap) : missed;
            next = kept[0];
        }

        var updated = task with
        {
            NextScheduledAt = next ?? occurrence,
            Status = next is null ? ScheduleTaskStatus.Inactive : ScheduleTaskStatus.Active,
            LastDeliveryAt = delivered ? occurrence : task.LastDeliveryAt,
            Attempt = 0,
            LastError = null,
        };

        await PersistTaskAsync(updated, ct).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(ScheduleTask task, Exception error, CancellationToken ct)
    {
        var attempt = task.Attempt + 1;
        _retryAt[task.TaskId] = _clock.GetUtcNow() + RetryDelay(attempt);

        // The obligation is kept: NextScheduledAt is unchanged, so the task is due again next pass.
        var updated = task with
        {
            Attempt = attempt,
            LastError = error.Message,
            Status = attempt >= _options.MaxAttempts ? ScheduleTaskStatus.Failed : task.Status,
        };

        await PersistTaskAsync(updated, ct).ConfigureAwait(false);
    }

    private async Task PersistTaskAsync(ScheduleTask task, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _tasks.Append(JsonSerializer.Serialize(TaskRecord.From(task), Json));
        _tasks.FlushToDisk();
        _appendsSinceCompact++;
        _byId[task.TaskId] = task;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task AppendReceiptAsync(DeliveryReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _receipts.Append(JsonSerializer.Serialize(receipt, Json));
        _receipts.FlushToDisk();
        _appendsSinceCompact++;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Under <see cref="CatchUpPolicy.All"/>, more occurrences can be overdue than the cap allows. The oldest are
    /// dropped (counted as missed) by moving the task to the first one that is kept. True when it moved.
    /// </summary>
    private async Task<bool> DropBeyondCapAsync(ScheduleTask task, DateTimeOffset occurrence, DateTimeOffset now, CancellationToken ct)
    {
        var overdue = new List<DateTimeOffset> { occurrence };
        var next = task.Trigger.NextAfter(occurrence, Zone);
        while (next is not null && next <= now)
        {
            overdue.Add(next.Value);
            next = task.Trigger.NextAfter(next.Value, Zone);
        }

        if (overdue.Count <= _options.CatchUpAllCap)
        {
            return false;
        }

        var dropped = overdue.Count - _options.CatchUpAllCap;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_byId.TryGetValue(task.TaskId, out var current) || current.NextScheduledAt != occurrence)
            {
                return true; // changed under us; the next pass looks again
            }

            await PersistTaskAsync(
                current with
                {
                    NextScheduledAt = overdue[dropped],
                    MissedCount = current.MissedCount + dropped,
                    LastMissedAt = overdue[dropped - 1],
                },
                ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeZoneInfo? _zone;
    private DateTimeOffset _zoneCheckedAt = DateTimeOffset.MinValue;

    /// <summary>The zone cron expressions are read in right now.</summary>
    private TimeZoneInfo Zone => _zone ??= _options.TimeZone;

    /// <summary>
    /// Notices a changed system zone and moves every cron task onto it: a task stored as "next 09:00" in the old
    /// zone would otherwise fire at 09:00 of a place the machine is no longer in. Called under the gate.
    /// </summary>
    private async Task RefreshZoneAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_options.TimeZoneSource is not { } source || now - _zoneCheckedAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _zoneCheckedAt = now;
        var current = source();
        if (_zone is null)
        {
            _zone = current;
            return;
        }

        if (string.Equals(_zone.Id, current.Id, StringComparison.Ordinal) && _zone.BaseUtcOffset == current.BaseUtcOffset)
        {
            return;
        }

        _zone = current;
        foreach (var task in _byId.Values.Where(t => t.Trigger is CronTrigger && t.Status != ScheduleTaskStatus.Inactive).ToList())
        {
            if (task.Trigger.NextAfter(now, current) is { } next && next != task.NextScheduledAt)
            {
                await PersistTaskAsync(task with { NextScheduledAt = next }, ct).ConfigureAwait(false);
            }
        }

        RecomputeNextDue();
    }

    private int _appendsSinceCompact;

    /// <summary>How many appends are let through before the logs are looked at again while running.</summary>
    private const int CompactCheckEvery = 500;

    /// <summary>
    /// Keeps the two logs from growing for the life of the installation. Every change appends a full task snapshot,
    /// so a task that fires every minute writes a line a minute; the file is replayed in full at every start. Last
    /// wins on replay, so the log is rewritten to the last line of each task. Receipts older than the retention
    /// window are dropped for the same reason: nothing can deliver an occurrence that old again.
    /// </summary>
    private void CompactLogs(DateTimeOffset now)
    {
        _appendsSinceCompact = 0;

        var taskLines = _tasks.ReadLines();
        if (taskLines.Count > Math.Max(_options.CompactTaskLogAbove, _byId.Count * 4))
        {
            _tasks.Rewrite(_byId.Values
                .OrderBy(task => task.TaskId, StringComparer.Ordinal)
                .Select(task => JsonSerializer.Serialize(TaskRecord.From(task), Json))
                .ToList());
        }

        var receiptLines = _receipts.ReadLines();
        var keep = new List<string>(receiptLines.Count);
        var oldest = now - _options.ReceiptRetention;

        foreach (var line in receiptLines)
        {
            var receipt = TryDeserialize<DeliveryReceipt>(line);
            if (receipt is null)
            {
                continue;
            }

            if (receipt.OccurrenceAt >= oldest)
            {
                keep.Add(line);
            }
            else
            {
                _receiptKeys.Remove(receipt.Key);
            }
        }

        if (keep.Count < receiptLines.Count)
        {
            _receipts.Rewrite(keep);
        }
    }

    private DateTimeOffset EffectiveDueAt(ScheduleTask task) =>
        _retryAt.TryGetValue(task.TaskId, out var retryAt) && retryAt > task.NextScheduledAt ? retryAt : task.NextScheduledAt;

    private TimeSpan RetryDelay(int attempt)
    {
        // base * 2^(attempt-1), capped; the exponent is clamped first so the multiplication cannot overflow.
        var factor = Math.Pow(2, Math.Min(attempt - 1, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(_options.RetryBaseDelay.Ticks * factor, _options.RetryMaxDelay.Ticks));
        return delay < _options.RetryBaseDelay ? _options.RetryBaseDelay : delay;
    }

    private void RecomputeNextDue()
    {
        DateTimeOffset? min = null;
        foreach (var task in _byId.Values)
        {
            if (task.Status == ScheduleTaskStatus.Inactive)
            {
                continue;
            }

            if (min is null || EffectiveDueAt(task) < min.Value)
            {
                min = EffectiveDueAt(task);
            }
        }

        _nextDueAt = min;
    }

    private void Replay()
    {
        foreach (var line in _tasks.ReadLines())
        {
            var record = TryDeserialize<TaskRecord>(line);
            if (record is { Status: TombstoneStatus })
            {
                _byId.Remove(record.TaskId);
                continue;
            }

            var task = record?.ToTask();
            if (task is not null)
            {
                _byId[task.TaskId] = task;
            }
        }

        foreach (var line in _receipts.ReadLines())
        {
            var receipt = TryDeserialize<DeliveryReceipt>(line);
            if (receipt is not null)
            {
                _receiptKeys.Add(receipt.Key);
            }
        }
    }

    private static T? TryDeserialize<T>(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string ReceiptKey(string taskId, DateTimeOffset occurrence) =>
        $"{taskId}@{occurrence.UtcDateTime:O}";

    private static string ItemId(string taskId, DateTimeOffset occurrence) =>
        $"{taskId}:{occurrence.UtcDateTime:O}";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Persisted form of a task. The trigger is encoded explicitly (<c>kind</c> + <c>spec</c>) instead of
    /// polymorphic JSON, so the on-disk format stays stable and readable.
    /// </summary>
    private sealed record TaskRecord(
        string TaskId,
        string TriggerKind,
        string TriggerSpec,
        string Payload,
        string Status,
        DateTimeOffset NextScheduledAt,
        DateTimeOffset? LastDeliveryAt,
        int Attempt,
        string? LastError,
        string? CatchUp = null,
        int MissedCount = 0,
        DateTimeOffset? LastMissedAt = null,
        DateTimeOffset? LastRunAt = null,
        bool? LastRunOk = null,
        string? LastRunError = null)
    {
        public static TaskRecord Tombstone(string taskId) =>
            new(taskId, "none", string.Empty, string.Empty, TombstoneStatus, default, null, 0, null);

        public static TaskRecord From(ScheduleTask task) => task.Trigger switch
        {
            OnceTrigger once => new TaskRecord(
                task.TaskId, "once", once.At.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                task.Payload, task.Status.ToString(),
                task.NextScheduledAt, task.LastDeliveryAt, task.Attempt, task.LastError,
                task.CatchUp.ToString(), task.MissedCount, task.LastMissedAt,
                task.LastRunAt, task.LastRunOk, task.LastRunError),
            CronTrigger cron => new TaskRecord(
                task.TaskId, "cron", cron.Expression, task.Payload, task.Status.ToString(),
                task.NextScheduledAt, task.LastDeliveryAt, task.Attempt, task.LastError,
                task.CatchUp.ToString(), task.MissedCount, task.LastMissedAt,
                task.LastRunAt, task.LastRunOk, task.LastRunError),
            IntervalTrigger interval => new TaskRecord(
                task.TaskId, "interval", interval.Interval.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                task.Payload, task.Status.ToString(),
                task.NextScheduledAt, task.LastDeliveryAt, task.Attempt, task.LastError,
                task.CatchUp.ToString(), task.MissedCount, task.LastMissedAt,
                task.LastRunAt, task.LastRunOk, task.LastRunError),
            _ => throw new NotSupportedException($"Unsupported trigger type {task.Trigger.GetType().Name}."),
        };

        public ScheduleTask? ToTask()
        {
            ScheduleTrigger? trigger = TriggerKind switch
            {
                "cron" => new CronTrigger(TriggerSpec),
                "once" when DateTimeOffset.TryParse(
                    TriggerSpec, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    => new OnceTrigger(at),
                "interval" when long.TryParse(
                    TriggerSpec, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                    => new IntervalTrigger(TimeSpan.FromTicks(ticks)),
                _ => null,
            };

            if (trigger is null || !Enum.TryParse<ScheduleTaskStatus>(Status, out var status))
            {
                return null;
            }

            return new ScheduleTask
            {
                TaskId = TaskId,
                Trigger = trigger,
                Payload = Payload,
                Status = status,
                NextScheduledAt = NextScheduledAt,
                LastDeliveryAt = LastDeliveryAt,
                Attempt = Attempt,
                LastError = LastError,
                CatchUp = Enum.TryParse<CatchUpPolicy>(CatchUp, out var policy) ? policy : CatchUpPolicy.Once,
                MissedCount = MissedCount,
                LastMissedAt = LastMissedAt,
                LastRunAt = LastRunAt,
                LastRunOk = LastRunOk,
                LastRunError = LastRunError,
            };
        }
    }
}
