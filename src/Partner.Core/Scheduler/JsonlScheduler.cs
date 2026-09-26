using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Storage;

namespace Partner.Core.Scheduler;

public sealed record SchedulerOptions
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

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
}

/// <summary>
/// Durable scheduler over two append-only logs (design doc §2.4).
/// <para>
/// State lives in <c>&lt;directory&gt;/scheduler/tasks.jsonl</c> (one full task snapshot per change, last wins)
/// and <c>&lt;directory&gt;/scheduler/receipts.jsonl</c> (one record per delivered occurrence). The due set is
/// always filtered from the replayed state, so the timer only decides <i>when to look</i>, never <i>what is due</i>.
/// </para>
/// </summary>
public sealed class JsonlScheduler : IScheduler
{
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
    private readonly SemaphoreSlim _gate = new(1, 1);
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = task;
            if (task.NextScheduledAt == default)
            {
                var next = task.Trigger.NextAfter(_clock.GetUtcNow(), _options.TimeZone)
                    ?? throw new InvalidOperationException(
                        $"Trigger '{task.Trigger.Describe()}' has no occurrence in the search horizon.");

                stored = task with { NextScheduledAt = next };
            }

            await PersistTaskAsync(stored, ct).ConfigureAwait(false);
            RecomputeNextDue();
        }
        finally
        {
            _gate.Release();
        }

        await RequestDriveAsync(ct).ConfigureAwait(false);
        return _byId[task.TaskId];
    }

    public async Task<ScheduleTask> SetStatusAsync(string taskId, ScheduleTaskStatus status, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_byId.TryGetValue(taskId, out var task))
            {
                throw new InvalidOperationException($"Unknown task '{taskId}'.");
            }

            var updated = task with { Status = status };
            await PersistTaskAsync(updated, ct).ConfigureAwait(false);
            RecomputeNextDue();
        }
        finally
        {
            _gate.Release();
        }

        await RequestDriveAsync(ct).ConfigureAwait(false);
        return _byId[taskId];
    }

    public Task<IReadOnlyList<ScheduleTask>> ListAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<ScheduleTask> snapshot = _byId.Values
            .OrderBy(t => t.NextScheduledAt)
            .ThenBy(t => t.TaskId, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(snapshot);
    }

    public async Task<DeliveryOutcome> DeliverDueAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();

            // The due set comes from persisted state, not from the timer (design doc §2.4 rule 2).
            var due = _byId.Values
                .Where(t => t.Status != ScheduleTaskStatus.Inactive && t.NextScheduledAt <= now)
                .OrderBy(t => t.NextScheduledAt)
                .ThenBy(t => t.TaskId, StringComparer.Ordinal)
                .ToList();

            var delivered = 0;
            var failed = 0;
            var skipped = 0;

            foreach (var task in due)
            {
                ct.ThrowIfCancellationRequested();

                var occurrence = task.NextScheduledAt;
                var key = ReceiptKey(task.TaskId, occurrence);

                if (_receiptKeys.Contains(key))
                {
                    // Crash landed between the receipt and the state advance: finish the advance only.
                    skipped++;
                    await AdvanceAsync(task, occurrence, now, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var itemId = ItemId(task.TaskId, occurrence);
                    await _queue.EnqueueAsync(new InputItem
                    {
                        ItemId = itemId,
                        Source = InputSource.Schedule,
                        Payload = task.Payload,
                        IdempotencyKey = $"schedule:{task.TaskId}:{occurrence.UtcDateTime:O}",
                    }, ct).ConfigureAwait(false);

                    // Receipt is committed BEFORE the state advance; if this throws the task stays due.
                    await AppendReceiptAsync(new DeliveryReceipt
                    {
                        TaskId = task.TaskId,
                        OccurrenceAt = occurrence,
                        DeliveredAt = now,
                        ItemId = itemId,
                        Attempt = task.Attempt,
                    }, ct).ConfigureAwait(false);

                    _receiptKeys.Add(key);
                    await AdvanceAsync(task, occurrence, now, ct).ConfigureAwait(false);
                    delivered++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    await RecordFailureAsync(task, ex, ct).ConfigureAwait(false);
                }
            }

            RecomputeNextDue();
            return new DeliveryOutcome(delivered, failed, skipped, _nextDueAt);
        }
        finally
        {
            _gate.Release();
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

    private async Task AdvanceAsync(ScheduleTask task, DateTimeOffset occurrence, DateTimeOffset now, CancellationToken ct)
    {
        var next = task.Trigger.NextAfter(occurrence, _options.TimeZone);

        // Occurrences missed while the process was down are skipped, never replayed as a burst.
        while (next is not null && next <= now)
        {
            next = task.Trigger.NextAfter(next.Value, _options.TimeZone);
        }

        var updated = task with
        {
            NextScheduledAt = next ?? occurrence,
            Status = next is null ? ScheduleTaskStatus.Inactive : ScheduleTaskStatus.Active,
            LastDeliveryAt = occurrence,
            Attempt = 0,
            LastError = null,
        };

        await PersistTaskAsync(updated, ct).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(ScheduleTask task, Exception error, CancellationToken ct)
    {
        var attempt = task.Attempt + 1;

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
        _byId[task.TaskId] = task;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task AppendReceiptAsync(DeliveryReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _receipts.Append(JsonSerializer.Serialize(receipt, Json));
        _receipts.FlushToDisk();
        await Task.CompletedTask.ConfigureAwait(false);
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

            if (min is null || task.NextScheduledAt < min.Value)
            {
                min = task.NextScheduledAt;
            }
        }

        _nextDueAt = min;
    }

    private void Replay()
    {
        foreach (var line in _tasks.ReadLines())
        {
            var record = TryDeserialize<TaskRecord>(line);
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
        string? LastError)
    {
        public static TaskRecord From(ScheduleTask task) => task.Trigger switch
        {
            CronTrigger cron => new TaskRecord(
                task.TaskId, "cron", cron.Expression, task.Payload, task.Status.ToString(),
                task.NextScheduledAt, task.LastDeliveryAt, task.Attempt, task.LastError),
            IntervalTrigger interval => new TaskRecord(
                task.TaskId, "interval", interval.Interval.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                task.Payload, task.Status.ToString(),
                task.NextScheduledAt, task.LastDeliveryAt, task.Attempt, task.LastError),
            _ => throw new NotSupportedException($"Unsupported trigger type {task.Trigger.GetType().Name}."),
        };

        public ScheduleTask? ToTask()
        {
            ScheduleTrigger? trigger = TriggerKind switch
            {
                "cron" => new CronTrigger(TriggerSpec),
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
            };
        }
    }
}
