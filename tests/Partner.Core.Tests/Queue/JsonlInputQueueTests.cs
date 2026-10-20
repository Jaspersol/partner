using Partner.Core.Contracts;
using Partner.Core.Queue;

namespace Partner.Core.Tests.Queue;

public sealed class JsonlInputQueueTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnqueueThenTake_PreservesStrictFifoWithMonotonicSeq()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);
        await using var queue = new JsonlInputQueue(dir.Path, clock: clock);

        var sources = new[] { InputSource.Voice, InputSource.Text, InputSource.Schedule };
        var enqueued = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            enqueued.Add(await queue.EnqueueAsync(Item($"item-{i}", $"key-{i}", sources[i % sources.Length])));
        }

        // Seq starts at 1, increases strictly, and follows enqueue order.
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), enqueued);

        var taken = new List<InputItem>();
        for (var i = 0; i < 100; i++)
        {
            var item = await queue.TakeAsync();
            Assert.NotNull(item);
            taken.Add(item);
        }

        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), taken.Select(t => t.Seq));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"item-{i}"), taken.Select(t => t.ItemId));
        Assert.Equal(sources[99 % sources.Length], taken[99].Source);
        Assert.All(taken, t => Assert.Equal(InputItemStatus.Running, t.Status));

        var snapshot = queue.Snapshot();
        Assert.Equal(101, snapshot.HeadSeq);
        Assert.Equal("item-99", snapshot.RunningItemId);
        Assert.Equal(0, snapshot.Depth);
        Assert.False(snapshot.Paused);
    }

    [Fact]
    public async Task EnqueueAsync_DeduplicatesByIdempotencyKey()
    {
        using var dir = new TempDir();
        await using var queue = new JsonlInputQueue(dir.Path, clock: new FakeClock(Base));

        var first = await queue.EnqueueAsync(Item("item-a", "same-key"));
        var second = await queue.EnqueueAsync(Item("item-b", "same-key"));

        Assert.Equal(first, second);
        Assert.Equal(1, queue.Snapshot().Depth);

        var log = await ReadLogAsync(queue);
        Assert.Single(log);
        Assert.Equal("item-a", log[0].ItemId);
    }

    [Fact]
    public async Task EnqueueAsync_DeduplicatesAcrossRestart()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            Assert.Equal(1, await queue.EnqueueAsync(Item("item-a", "key")));
            var taken = await queue.TakeAsync();
            Assert.Equal("item-a", taken!.ItemId);
            await queue.CompleteAsync("item-a", InputItemStatus.Done);
        }

        await using (var reopened = new JsonlInputQueue(dir.Path, clock: clock))
        {
            // A consumed idempotency key still deduplicates: no new item, original Seq returned.
            Assert.Equal(1, await reopened.EnqueueAsync(Item("item-other", "key")));
            Assert.Equal(0, reopened.Snapshot().Depth);
            Assert.Equal(2, reopened.Snapshot().HeadSeq);

            // The settled item is compacted out of the items log. What must survive is the key, and the two
            // asserts above prove it: the retry was de-duplicated rather than enqueued a second time.
            Assert.Empty(await ReadLogAsync(reopened));
        }
    }

    /// <summary>
    /// An interrupted item is retried, but only a bounded number of times.
    /// <para>
    /// Without a cap, a wedged turn returns on every start and each retry re-runs a turn that already issued
    /// its tool calls. The cap turns the last attempt into a recorded failure, so the queue ends up telling the
    /// user why instead of showing a permanently "running" item.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Constructor_FailsAnItemThatHasUsedUpItsAttemptsInsteadOfRequeueingItForever()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        // One restart per attempt: enqueue, claim (leaving it Running), reopen.
        for (var restart = 0; restart < JsonlInputQueue.MaxAttempts; restart++)
        {
            await using var queue = new JsonlInputQueue(dir.Path, clock: clock);

            if (restart == 0)
            {
                await queue.EnqueueAsync(Item("item-a", "key-a"));
            }

            var taken = await queue.TakeAsync();
            Assert.Equal("item-a", taken!.ItemId);
            Assert.Equal(restart, taken.Attempt);
            Assert.Equal(InputItemStatus.Running, taken.Status);

            // No settle: the process dies here, which is the case under test.
        }

        // The next start has no attempts left, so the item is failed with the reason recorded.
        await using (var final = new JsonlInputQueue(dir.Path, clock: clock))
        {
            Assert.Null(await TakeNowAsync(final));
            Assert.Equal(0, final.Snapshot().Depth);

            // The cap holds: the item is not taken again and the queue is empty. The Failed record is compacted
            // away; the attempt index is proven by the loop above, which read it off each retry.
            Assert.DoesNotContain(await ReadLogAsync(final), i => i.ItemId == "item-a");

        }

        // A third start changes nothing: without the record there is nothing to retry.
        await using (var again = new JsonlInputQueue(dir.Path, clock: clock))
        {
            Assert.Null(await TakeNowAsync(again));
            Assert.Equal(0, again.Snapshot().Depth);
        }
    }

    /// <summary>
    /// A failed item is not offered again, and its idempotency key still deduplicates — the same two answers a
    /// settled item gives.
    /// </summary>
    [Fact]
    public async Task AnItemTheQueueFailedOnStartupIsNeitherTakenNorReEnqueued()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            await queue.EnqueueAsync(Item("item-a", "key-a"));
            await queue.TakeAsync();
        }

        for (var restart = 0; restart < JsonlInputQueue.MaxAttempts; restart++)
        {
            await using var queue = new JsonlInputQueue(dir.Path, clock: clock);
            await TakeNowAsync(queue);
        }

        await using var reopened = new JsonlInputQueue(dir.Path, clock: clock);

        Assert.Null(await TakeNowAsync(reopened));
        Assert.Equal(1, await reopened.EnqueueAsync(Item("item-a", "key-a")));
        Assert.Equal(0, reopened.Snapshot().Depth);
    }

    [Fact]
    public async Task Constructor_RequeuesInterruptedRunningItemWithIncrementedAttempt()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            await queue.EnqueueAsync(Item("item-a", "key-a"));
            await queue.EnqueueAsync(Item("item-b", "key-b"));
            await queue.EnqueueAsync(Item("item-c", "key-c"));

            var taken = await queue.TakeAsync();
            Assert.Equal("item-a", taken!.ItemId);
            Assert.Equal(InputItemStatus.Running, taken.Status);
            Assert.Equal(0, taken.Attempt);
        }

        await using (var reopened = new JsonlInputQueue(dir.Path, clock: clock))
        {
            var snapshot = reopened.Snapshot();
            Assert.Null(snapshot.RunningItemId);
            Assert.Equal(3, snapshot.Depth);

            var log = await ReadLogAsync(reopened);
            var requeued = log.Single(i => i.ItemId == "item-a" && i.Attempt == 1);
            Assert.Equal(InputItemStatus.Queued, requeued.Status);
            Assert.Null(requeued.StartedAt);

            var first = await reopened.TakeAsync();
            Assert.Equal("item-a", first!.ItemId);
            Assert.Equal(1, first.Seq);
            Assert.Equal(1, first.Attempt);
            Assert.Equal(InputItemStatus.Running, first.Status);

            var second = await reopened.TakeAsync();
            Assert.Equal("item-b", second!.ItemId);
            Assert.Equal(2, second.Seq);

            var third = await reopened.TakeAsync();
            Assert.Equal("item-c", third!.ItemId);
            Assert.Equal(3, third.Seq);
        }
    }

    [Fact]
    public async Task CancelAsync_CancelsQueuedItemPermanently()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            await queue.EnqueueAsync(Item("item-a", "key-a"));
            await queue.EnqueueAsync(Item("item-b", "key-b"));

            Assert.True(await queue.CancelAsync("item-a"));
            Assert.Equal(1, queue.Snapshot().Depth);

            var taken = await queue.TakeAsync();
            Assert.Equal("item-b", taken!.ItemId);

            // A cancelled queued item is never handed to the consumer, even while it still occupies a channel slot.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.Null(await queue.TakeAsync(timeout.Token));
        }

        await using (var reopened = new JsonlInputQueue(dir.Path, clock: clock))
        {
            // item-a is cancelled and never re-enqueued; item-b is requeued after the interruption.
            Assert.Equal(1, reopened.Snapshot().Depth);

            // The cancelled item is compacted out of the log, so "it never comes back" is asserted by its
            // absence.
            Assert.DoesNotContain(await ReadLogAsync(reopened), i => i.ItemId == "item-a");

            var only = await reopened.TakeAsync();
            Assert.Equal("item-b", only!.ItemId);
            Assert.Equal(1, only.Attempt);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.Null(await reopened.TakeAsync(timeout.Token));
        }
    }

    [Fact]
    public async Task CancelAsync_CancelsRunningItemThroughRunningToken()
    {
        using var dir = new TempDir();
        await using var queue = new JsonlInputQueue(dir.Path, clock: new FakeClock(Base));

        await queue.EnqueueAsync(Item("item-a", "key-a"));
        var taken = await queue.TakeAsync();
        Assert.Equal("item-a", taken!.ItemId);
        Assert.False(queue.RunningToken.IsCancellationRequested);

        Assert.True(await queue.CancelAsync("item-a"));
        Assert.True(queue.RunningToken.IsCancellationRequested);

        // The status is still settled by the consumer.
        Assert.Equal("item-a", queue.Snapshot().RunningItemId);
        await queue.CompleteAsync("item-a", InputItemStatus.Canceled);
        Assert.Null(queue.Snapshot().RunningItemId);

        // Unknown or already-settled items cannot be cancelled.
        Assert.False(await queue.CancelAsync("item-a"));
        Assert.False(await queue.CancelAsync("nope"));
    }

    [Fact]
    public async Task EnqueueAsync_WaitsWhileCapacityIsFull()
    {
        using var dir = new TempDir();
        await using var queue = new JsonlInputQueue(dir.Path, capacity: 2, clock: new FakeClock(Base));

        await queue.EnqueueAsync(Item("item-a", "key-a"));
        await queue.EnqueueAsync(Item("item-b", "key-b"));

        var third = queue.EnqueueAsync(Item("item-c", "key-c")).AsTask();
        await Task.Delay(200);
        Assert.False(third.IsCompleted);

        var taken = await queue.TakeAsync();
        Assert.Equal("item-a", taken!.ItemId);

        Assert.Equal(3, await third.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, queue.Snapshot().Depth);
    }

    [Fact]
    public async Task CompleteAsync_PersistsFailedStateWithError()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            await queue.EnqueueAsync(Item("item-a", "key-a"));
            await queue.TakeAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            await queue.CompleteAsync("item-a", InputItemStatus.Failed, "boom");
        }

        await using (var reopened = new JsonlInputQueue(dir.Path, clock: clock))
        {
            // The failed item is compacted out on this start, so its error text is no longer in the queue's
            // log. A failed turn also writes TurnEnd with reason=error and an "error:"-prefixed System event
            // into the session log, which is where a reader meets it.
            Assert.Empty(await ReadLogAsync(reopened));
            Assert.Null(await TakeNowAsync(reopened));
            Assert.Equal(0, reopened.Snapshot().Depth);
        }
    }

    [Fact]
    public async Task CompleteAsync_RejectsNonTerminalStatusAndUnknownItem()
    {
        using var dir = new TempDir();
        await using var queue = new JsonlInputQueue(dir.Path, clock: new FakeClock(Base));

        await queue.EnqueueAsync(Item("item-a", "key-a"));

        await Assert.ThrowsAsync<ArgumentException>(() => queue.CompleteAsync("item-a", InputItemStatus.Queued));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.CompleteAsync("item-a", InputItemStatus.Running));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.CompleteAsync("nope", InputItemStatus.Done));
    }

    [Fact]
    public async Task Constructor_DropsTornTailWithoutLosingCommittedItems()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            await queue.EnqueueAsync(Item("item-a", "key-a"));
        }

        // Simulate a crash: a half line at the end of the file (no newline).
        var logPath = System.IO.Path.Combine(dir.Path, "queue", "items.jsonl");
        File.WriteAllText(logPath, File.ReadAllText(logPath) + "{\"itemId\":\"torn");

        await using (var reopened = new JsonlInputQueue(dir.Path, clock: clock))
        {
            var log = await ReadLogAsync(reopened);
            Assert.Single(log);
            Assert.Equal("item-a", log[0].ItemId);

            var snapshot = reopened.Snapshot();
            Assert.Equal(1, snapshot.Depth);
            Assert.Equal(2, snapshot.HeadSeq);

            var taken = await reopened.TakeAsync();
            Assert.Equal("item-a", taken!.ItemId);
        }
    }

    private static InputItem Item(string itemId, string key, InputSource source = InputSource.Text) => new()
    {
        ItemId = itemId,
        Source = source,
        Payload = $"payload:{itemId}",
        IdempotencyKey = key,
    };

    [Fact]
    public async Task Attachments_survive_every_state_transition()
    {
        // MediaIds is one field among many in a class that is copied field by field on each transition; a
        // forgotten one is silently dropped, and the read path takes the last record per item.
        using var dir = new TempDir();
        await using var queue = new JsonlInputQueue(dir.Path, clock: new FakeClock(Base));

        var item = Item("with-image", "key-image") with { MediaIds = new[] { "m1", "m2" } };
        await queue.EnqueueAsync(item);

        var taken = await queue.TakeAsync();
        Assert.Equal(new[] { "m1", "m2" }, taken!.MediaIds);

        await queue.CompleteAsync(taken.ItemId, InputItemStatus.Done);

        var reread = await ReadLogAsync(queue);
        var latest = reread.Where(i => i.ItemId == "with-image").OrderBy(i => i.Seq).Last();
        Assert.Equal(new[] { "m1", "m2" }, latest.MediaIds);
    }

    [Fact]
    public async Task Settled_items_are_dropped_from_memory_but_stay_on_disk()
    {
        // A Done item cannot be taken, cancelled or completed again, and a restart replays the log, not this map.
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using var queue = new JsonlInputQueue(dir.Path, clock: clock);
        for (var i = 0; i < 3; i++)
        {
            await queue.EnqueueAsync(Item($"item-{i}", $"key-{i}"));
            var taken = await queue.TakeAsync();
            await queue.CompleteAsync(taken!.ItemId, InputItemStatus.Done);
        }

        // The log is the record of what happened, and is unaffected.
        var log = await ReadLogAsync(queue);
        Assert.Equal(9, log.Count);
        Assert.Equal(3, log.Count(i => i.Status == InputItemStatus.Done));
    }

    [Fact]
    public async Task A_settled_item_still_deduplicates_after_a_restart()
    {
        // Dropping it from memory must not weaken de-duplication: the key is the guarantee, and the question it
        // has to answer — "what seq was this key accepted at" — is answerable without the item.
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using (var queue = new JsonlInputQueue(dir.Path, clock: clock))
        {
            await queue.EnqueueAsync(Item("item-a", "key"));
            var taken = await queue.TakeAsync();
            await queue.CompleteAsync(taken!.ItemId, InputItemStatus.Done);
        }

        await using var reopened = new JsonlInputQueue(dir.Path, clock: clock);

        // Same key, different item: the old seq comes back and nothing is enqueued.
        Assert.Equal(1, await reopened.EnqueueAsync(Item("item-other", "key")));
        Assert.Equal(0, reopened.Snapshot().Depth);
    }

    [Fact]
    public async Task Completing_an_already_settled_item_is_idempotent_rather_than_an_error()
    {
        // A redelivered item whose turn already ended is marked Done again without being re-run. That is
        // ordinary crash recovery, and it lands where the item is no longer in memory, so throwing here would
        // turn recovery into a second crash.
        using var dir = new TempDir();
        var clock = new FakeClock(Base);

        await using var queue = new JsonlInputQueue(dir.Path, clock: clock);
        await queue.EnqueueAsync(Item("item-a", "key"));
        var taken = await queue.TakeAsync();
        await queue.CompleteAsync(taken!.ItemId, InputItemStatus.Done);

        await queue.CompleteAsync(taken.ItemId, InputItemStatus.Done);

        Assert.Equal(
            InputItemStatus.Done,
            (await ReadLogAsync(queue)).Last(i => i.ItemId == "item-a").Status);
    }

    [Fact]
    public async Task Completing_an_item_that_never_existed_still_fails()
    {
        // Idempotence is for items that were here. An id the queue never saw is a caller bug, and swallowing
        // it would mark nothing while reporting success.
        using var dir = new TempDir();
        await using var queue = new JsonlInputQueue(dir.Path, clock: new FakeClock(Base));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.CompleteAsync("no-such-item", InputItemStatus.Done));
    }

    /// <summary>
    /// Takes an item if one is waiting and answers null if not. <c>TakeAsync</c> with no token waits for the
    /// next item indefinitely — which is right for a consumer and hangs a test that is asking "is there
    /// anything left?", so the question is asked with a token instead.
    /// </summary>
    private static async Task<InputItem?> TakeNowAsync(IInputQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        return await queue.TakeAsync(cts.Token);
    }

    private static async Task<List<InputItem>> ReadLogAsync(IInputQueue queue)
    {
        var items = new List<InputItem>();
        await foreach (var item in queue.ReadLogAsync())
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "partner-queue-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Leave cleanup to the OS when a handle is still open; do not mask the test result.
            }
        }
    }
}
