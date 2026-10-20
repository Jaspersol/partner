using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Scheduler;
using Partner.Core.Storage;
using Partner.Core.Tests.Sessions;

namespace Partner.Core.Tests.Scheduler;

/// <summary>Records what the scheduler delivered; can be told to fail.</summary>
internal sealed class RecordingQueue : IInputQueue
{
    private readonly List<InputItem> _items = new();
    private readonly Dictionary<string, long> _byKey = new(StringComparer.Ordinal);

    public bool FailNextEnqueue { get; set; }

    public int FailuresRemaining { get; set; }

    public bool AlwaysFail { get; set; }

    public int EnqueueCalls { get; private set; }

    public IReadOnlyList<InputItem> Items => _items;

    /// <summary>While set and unfinished, an enqueue waits on it: a queue with no free slot.</summary>
    public TaskCompletionSource? Block { get; set; }

    public async ValueTask<long> EnqueueAsync(InputItem item, CancellationToken ct = default)
    {
        EnqueueCalls++;
        if (Block is { } block)
        {
            await block.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (AlwaysFail)
        {
            throw new IOException("simulated enqueue failure");
        }

        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new IOException("simulated enqueue failure");
        }

        if (FailNextEnqueue)
        {
            FailNextEnqueue = false;
            throw new IOException("simulated enqueue failure");
        }

        if (_byKey.TryGetValue(item.IdempotencyKey, out var existing))
        {
            return existing;
        }

        var seq = (long)(_items.Count + 1);
        _items.Add(new InputItem
        {
            ItemId = item.ItemId,
            Seq = seq,
            EnqueuedAt = item.EnqueuedAt,
            Source = item.Source,
            Payload = item.Payload,
            IdempotencyKey = item.IdempotencyKey,
            Status = InputItemStatus.Queued,
            Attempt = item.Attempt,
        });
        _byKey[item.IdempotencyKey] = seq;
        return seq;
    }

    public ValueTask<InputItem?> TakeAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(_items.FirstOrDefault(i => i.Status == InputItemStatus.Queued));

    public QueueState Snapshot() => new(_items.Count + 1, null, _items.Count, false);

    public IReadOnlyList<InputItem> ActiveItems() => _items.ToList();

    public Task<bool> CancelAsync(string itemId, CancellationToken ct = default) => Task.FromResult(false);

    public Task CompleteAsync(string itemId, InputItemStatus status, string? error = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public async IAsyncEnumerable<InputItem> ReadLogAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public CancellationToken RunningToken => CancellationToken.None;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Acceptance tests for the durable scheduler (design doc §2.4 / Phase 2).</summary>
public sealed class JsonlSchedulerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ScheduleTask Every(string id, TimeSpan interval, DateTimeOffset from, string payload = "tick") => new()
    {
        TaskId = id,
        Trigger = new IntervalTrigger(interval),
        Payload = payload,
        NextScheduledAt = from,
    };

    [Fact]
    public async Task Due_task_is_delivered_once_and_advances()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));

        var outcome = await scheduler.DeliverDueAsync();

        Assert.Equal(1, outcome.Delivered);
        var item = Assert.Single(queue.Items);
        Assert.Equal(InputSource.Schedule, item.Source);
        Assert.Equal("tick", item.Payload);
        Assert.Equal("schedule:t1:2026-09-26T10:00:00.0000000Z", item.IdempotencyKey);

        var task = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), task.NextScheduledAt);
        Assert.Equal(clock.GetUtcNow(), task.LastDeliveryAt);
        Assert.Equal(ScheduleTaskStatus.Active, task.Status);

        // Nothing is due a second time at the same instant.
        var again = await scheduler.DeliverDueAsync();
        Assert.Equal(0, again.Delivered);
        Assert.Single(queue.Items);
    }

    [Fact]
    public async Task Future_task_is_not_fired_early_after_a_clock_rollback()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromHours(1), clock.GetUtcNow().AddHours(1)));

        Assert.Equal(0, (await scheduler.DeliverDueAsync()).Delivered);

        // Clock jumps backwards: the task must still not fire.
        clock.Advance(TimeSpan.FromHours(-3));
        Assert.Equal(0, (await scheduler.DeliverDueAsync()).Delivered);
        Assert.Empty(queue.Items);

        // ... and fires once the original instant is reached again.
        clock.Advance(TimeSpan.FromHours(4));
        Assert.Equal(1, (await scheduler.DeliverDueAsync()).Delivered);
        Assert.Single(queue.Items);
    }

    [Fact]
    public async Task Failed_delivery_keeps_the_obligation_and_is_flagged_after_max_attempts()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue { FailuresRemaining = 5 };
        await using var scheduler = new JsonlScheduler(
            _dir, queue, clock, new SchedulerOptions { MaxAttempts = 5 });

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));

        var dueAt = clock.GetUtcNow();

        for (var i = 1; i <= 5; i++)
        {
            if (i > 1)
            {
                clock.Advance(TimeSpan.FromMinutes(10)); // past the retry backoff
            }

            var outcome = await scheduler.DeliverDueAsync();
            Assert.Equal(1, outcome.Failed);
            Assert.Equal(0, outcome.Delivered);

            var task = Assert.Single(await scheduler.ListAsync());
            Assert.Equal(i, task.Attempt);
            Assert.Equal(dueAt, task.NextScheduledAt); // unchanged: still owed
            Assert.Equal(i >= 5 ? ScheduleTaskStatus.Failed : ScheduleTaskStatus.Active, task.Status);
        }

        Assert.Empty(queue.Items);

        // A failing task is never lost: it is still in the due set and recovers when the queue does.
        clock.Advance(TimeSpan.FromMinutes(10));
        var recovered = await scheduler.DeliverDueAsync();
        Assert.Equal(1, recovered.Delivered);
        var healed = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(0, healed.Attempt);
        Assert.Null(healed.LastError);
        Assert.Equal(ScheduleTaskStatus.Active, healed.Status);
    }

    [Fact]
    public async Task Receipt_write_failure_counts_as_a_failed_delivery_and_keeps_the_task_due()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        var tasks = new FakeSink();
        var receipts = new FakeSink();

        await using var scheduler = new JsonlScheduler(tasks, receipts, queue, clock);
        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));

        receipts.FailNextFlush();

        // A receipt failure is a failed delivery, not a pass-level abort: other due tasks must still run.
        var outcome = await scheduler.DeliverDueAsync();

        Assert.Equal(0, outcome.Delivered);
        Assert.Equal(1, outcome.Failed);

        var task = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(clock.GetUtcNow(), task.NextScheduledAt); // unchanged: still due
        Assert.Null(task.LastDeliveryAt);
        Assert.Equal(1, task.Attempt);
        Assert.Contains("simulated flush failure", task.LastError);

        // The item did reach the queue before the receipt failed; the retry must not duplicate it.
        Assert.Single(queue.Items);
        clock.Advance(TimeSpan.FromSeconds(2)); // past the retry backoff
        var retry = await scheduler.DeliverDueAsync();
        Assert.Equal(1, retry.Delivered);
        Assert.Single(queue.Items); // the queue's idempotency key absorbed the retry

        var healed = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(0, healed.Attempt);
        Assert.Equal(clock.GetUtcNow().AddSeconds(-2).AddMinutes(1), healed.NextScheduledAt);
    }

    [Fact]
    public async Task Restart_replays_tasks_and_never_redelivers_a_receipted_occurrence()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();

        await using (var first = new JsonlScheduler(_dir, queue, clock))
        {
            await first.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));
            Assert.Equal(1, (await first.DeliverDueAsync()).Delivered);
        }

        await using var second = new JsonlScheduler(_dir, queue, clock);
        var tasks = await second.ListAsync();
        var task = Assert.Single(tasks);
        Assert.Equal("t1", task.TaskId);
        Assert.IsType<IntervalTrigger>(task.Trigger);
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), task.NextScheduledAt);

        // Nothing due at the same instant, and no duplicate delivery.
        Assert.Equal(0, (await second.DeliverDueAsync()).Delivered);
        Assert.Single(queue.Items);

        // Next occurrence delivers once.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await second.DeliverDueAsync()).Delivered);
        Assert.Equal(2, queue.Items.Count);
    }

    [Fact]
    public async Task Crash_between_receipt_and_advance_only_advances()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        var tasks = new FakeSink();
        var receipts = new FakeSink();

        // Hand-craft the crash state: a receipt exists, the task state was never advanced.
        await using (var first = new JsonlScheduler(tasks, receipts, queue, clock))
        {
            await first.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));
        }

        var task = Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow());
        var receipt = new DeliveryReceipt
        {
            TaskId = "t1",
            OccurrenceAt = clock.GetUtcNow(),
            DeliveredAt = clock.GetUtcNow(),
            ItemId = "t1:crash",
        };

        var json = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        receipts.Append(System.Text.Json.JsonSerializer.Serialize(receipt, json));
        receipts.FlushToDisk();

        await using var restarted = new JsonlScheduler(tasks, receipts, queue, clock);
        var outcome = await restarted.DeliverDueAsync();

        Assert.Equal(0, outcome.Delivered);
        Assert.Equal(1, outcome.SkippedAlreadyDelivered);
        Assert.Empty(queue.Items);

        var advanced = Assert.Single(await restarted.ListAsync());
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), advanced.NextScheduledAt);
        Assert.Equal(clock.GetUtcNow(), advanced.LastDeliveryAt);
    }

    [Fact]
    public async Task Missed_occurrences_are_skipped_not_replayed_as_a_burst()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));

        // The process was down for an hour.
        clock.Advance(TimeSpan.FromHours(1));

        var outcome = await scheduler.DeliverDueAsync();

        Assert.Equal(1, outcome.Delivered); // one catch-up delivery, not 60
        var task = Assert.Single(await scheduler.ListAsync());
        Assert.True(task.NextScheduledAt > clock.GetUtcNow(), $"next={task.NextScheduledAt:O}");
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), task.NextScheduledAt);
    }

    [Fact]
    public async Task Cron_task_persists_and_reloads()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();

        await using (var first = new JsonlScheduler(_dir, queue, clock))
        {
            await first.ScheduleAsync(new ScheduleTask
            {
                TaskId = "daily",
                Trigger = new CronTrigger("0 9 * * *"),
                Payload = "digest",
            });
        }

        await using var second = new JsonlScheduler(_dir, queue, clock);
        var task = Assert.Single(await second.ListAsync());
        var cron = Assert.IsType<CronTrigger>(task.Trigger);
        Assert.Equal("0 9 * * *", cron.Expression);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero), task.NextScheduledAt);
    }

    [Fact]
    public async Task Inactive_tasks_are_excluded_from_the_due_set_and_resume_on_request()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), clock.GetUtcNow()));
        await scheduler.SetStatusAsync("t1", ScheduleTaskStatus.Inactive);

        Assert.Null(scheduler.NextDueAt);
        Assert.Equal(0, (await scheduler.DeliverDueAsync()).Delivered);

        await scheduler.SetStatusAsync("t1", ScheduleTaskStatus.Active);
        Assert.Equal(clock.GetUtcNow(), scheduler.NextDueAt);
        Assert.Equal(1, (await scheduler.DeliverDueAsync()).Delivered);
    }

    [Fact]
    public async Task Schedule_rejects_empty_id_payload_and_exhausted_trigger()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.ScheduleAsync(new ScheduleTask
        {
            TaskId = " ",
            Trigger = new IntervalTrigger(TimeSpan.FromMinutes(1)),
            Payload = "x",
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.ScheduleAsync(new ScheduleTask
        {
            TaskId = "t",
            Trigger = new IntervalTrigger(TimeSpan.FromMinutes(1)),
            Payload = "",
        }));

        // Feb 30 never happens: the trigger is exhausted within the search horizon.
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ScheduleAsync(new ScheduleTask
        {
            TaskId = "never",
            Trigger = new CronTrigger("0 0 30 2 *"),
            Payload = "x",
        }));
    }

    [Fact]
    public async Task Driver_delivers_on_time_even_while_the_calling_thread_is_blocked()
    {
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, TimeProvider.System);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMilliseconds(60), DateTimeOffset.UtcNow));

        using var cts = new CancellationTokenSource();
        var driver = scheduler.RunAsync(cts.Token);

        // The regression the design doc cares about: blocking the caller for 2x the interval must not stop
        // delivery — the scheduler waits on its own OS-level handle, not on a shared event loop.
        Thread.Sleep(150);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver).ContinueWith(_ => { });

        Assert.True(queue.Items.Count >= 1, $"expected at least one delivery, got {queue.Items.Count}");
    }

    [Fact]
    public async Task RequestDrive_wakes_the_driver_for_a_newly_scheduled_task()
    {
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(
            _dir, queue, TimeProvider.System, new SchedulerOptions { IdleWait = TimeSpan.FromSeconds(30) });

        using var cts = new CancellationTokenSource();
        var driver = scheduler.RunAsync(cts.Token);

        // Nothing scheduled yet: the driver is parked on the long idle wait.
        await Task.Delay(60);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow));

        var delivered = false;
        for (var i = 0; i < 40 && !delivered; i++)
        {
            await Task.Delay(25);
            delivered = queue.Items.Count > 0;
        }

        await cts.CancelAsync();
        await Task.WhenAny(driver, Task.Delay(500));

        Assert.True(delivered, "RequestDriveAsync should have woken the driver well before the 30s idle wait");
    }

    [Fact]
    public async Task Once_task_fires_once_then_goes_inactive_and_survives_a_restart()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        var at = clock.GetUtcNow().AddMinutes(5);

        await using (var scheduler = new JsonlScheduler(_dir, queue, clock))
        {
            await scheduler.ScheduleAsync(new ScheduleTask { TaskId = "once", Trigger = new OnceTrigger(at), Payload = "once" });
            Assert.Equal(at, scheduler.NextDueAt);

            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(1, (await scheduler.DeliverDueAsync()).Delivered);
            Assert.Equal(ScheduleTaskStatus.Inactive, (await scheduler.ListAsync()).Single().Status);
            Assert.Null(scheduler.NextDueAt);
        }

        await using var restarted = new JsonlScheduler(_dir, queue, clock);
        var task = (await restarted.ListAsync()).Single();
        Assert.Equal(new OnceTrigger(at), task.Trigger);
        Assert.Equal(ScheduleTaskStatus.Inactive, task.Status);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await restarted.DeliverDueAsync()).Delivered);
        Assert.Single(queue.Items);
    }

    [Fact]
    public async Task Removed_task_stays_removed_after_a_restart_and_never_fires()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();

        await using (var scheduler = new JsonlScheduler(_dir, queue, clock))
        {
            await scheduler.ScheduleAsync(Every("keep", TimeSpan.FromMinutes(10), clock.GetUtcNow().AddMinutes(10)));
            await scheduler.ScheduleAsync(Every("drop", TimeSpan.FromMinutes(1), clock.GetUtcNow().AddMinutes(1)));

            Assert.True(await scheduler.RemoveAsync("drop"));
            Assert.False(await scheduler.RemoveAsync("drop"));
            Assert.Equal(clock.GetUtcNow().AddMinutes(10), scheduler.NextDueAt);
        }

        await using var restarted = new JsonlScheduler(_dir, queue, clock);
        Assert.Equal(["keep"], (await restarted.ListAsync()).Select(t => t.TaskId).ToArray());

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await restarted.DeliverDueAsync()).Delivered);
    }

    [Fact]
    public async Task Resuming_a_paused_task_skips_the_occurrences_that_passed_while_it_was_paused()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue();
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(10), clock.GetUtcNow().AddMinutes(10)));
        await scheduler.SetStatusAsync("t1", ScheduleTaskStatus.Inactive);

        clock.Advance(TimeSpan.FromMinutes(25));
        var resumed = await scheduler.SetStatusAsync("t1", ScheduleTaskStatus.Active);

        Assert.Equal(clock.GetUtcNow().AddMinutes(10), resumed.NextScheduledAt);
        Assert.Equal(0, (await scheduler.DeliverDueAsync()).Delivered);
    }

    [Fact]
    public async Task Resuming_a_failed_task_retries_the_delivery_it_owes()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var dueAt = clock.GetUtcNow();
        var queue = new RecordingQueue { FailuresRemaining = 2 };
        await using var scheduler = new JsonlScheduler(
            _dir, queue, clock, new SchedulerOptions { MaxAttempts = 2 });

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(10), clock.GetUtcNow()));
        await scheduler.DeliverDueAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.DeliverDueAsync();
        Assert.Equal(ScheduleTaskStatus.Failed, (await scheduler.ListAsync()).Single().Status);

        var resumed = await scheduler.SetStatusAsync("t1", ScheduleTaskStatus.Active);
        Assert.Equal(0, resumed.Attempt);
        Assert.Equal(dueAt, resumed.NextScheduledAt);
        Assert.Equal(1, (await scheduler.DeliverDueAsync()).Delivered);
    }

    [Fact]
    public async Task A_permanently_failing_queue_is_retried_with_backoff_not_every_pass()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue { AlwaysFail = true };
        await using var scheduler = new JsonlScheduler(_dir, queue, clock);

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromHours(1), clock.GetUtcNow()));

        // Ten seconds of a driver that looks every 50 ms: 1 s, 2 s, 4 s backoff allows 4 attempts at most.
        for (var elapsedMs = 0; elapsedMs < 10_000; elapsedMs += 50)
        {
            await scheduler.DeliverDueAsync();
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.InRange(queue.EnqueueCalls, 1, 5);
        var task = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(queue.EnqueueCalls, task.Attempt);

        // The wait the driver sleeps is the backoff, not the 50 ms floor.
        var outcome = await scheduler.DeliverDueAsync();
        Assert.True(outcome.NextDueAt > clock.GetUtcNow().AddMilliseconds(500));
    }

    [Fact]
    public async Task A_full_input_queue_does_not_freeze_listing_while_a_delivery_waits_for_room()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var scheduler = new JsonlScheduler(
            _dir, queue, clock, new SchedulerOptions { EnqueueTimeout = TimeSpan.FromSeconds(30) });

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(10), clock.GetUtcNow()));

        var delivery = Task.Run(() => scheduler.DeliverDueAsync());
        Assert.True(await Eventually(() => queue.EnqueueCalls > 0), "the delivery never reached the queue");

        // The pass is parked inside EnqueueAsync. Everything that only reads or edits tasks must still answer.
        var listing = scheduler.ListAsync();
        var finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(listing, finished);
        Assert.Single(await listing);

        queue.Block.SetResult();
        var outcome = await delivery;
        Assert.Equal(1, outcome.Delivered);
    }

    [Fact]
    public async Task A_queue_that_never_has_room_fails_the_attempt_and_backs_off_instead_of_waiting_forever()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var queue = new RecordingQueue { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var scheduler = new JsonlScheduler(
            _dir, queue, clock, new SchedulerOptions { EnqueueTimeout = TimeSpan.FromMilliseconds(100) });

        await scheduler.ScheduleAsync(Every("t1", TimeSpan.FromMinutes(10), clock.GetUtcNow()));

        var outcome = await scheduler.DeliverDueAsync();

        Assert.Equal(1, outcome.Failed);
        var task = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(1, task.Attempt);
        Assert.Contains("did not accept", task.LastError);
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}
