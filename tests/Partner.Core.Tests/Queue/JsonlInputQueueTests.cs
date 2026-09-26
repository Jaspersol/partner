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

        // Seq 从 1 开始、严格递增，且等于入队顺序。
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
            // 已消费过的幂等键仍参与去重：不产生新项，返回原 Seq。
            Assert.Equal(1, await reopened.EnqueueAsync(Item("item-other", "key")));
            Assert.Equal(0, reopened.Snapshot().Depth);
            Assert.Equal(2, reopened.Snapshot().HeadSeq);

            var log = await ReadLogAsync(reopened);
            Assert.Equal(
                new[] { InputItemStatus.Queued, InputItemStatus.Running, InputItemStatus.Done },
                log.Select(i => i.Status));
        }
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

            // 被取消的排队项永不交给消费者（即使它仍占据通道中的位置）。
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.Null(await queue.TakeAsync(timeout.Token));
        }

        await using (var reopened = new JsonlInputQueue(dir.Path, clock: clock))
        {
            // item-a 保持 Canceled 且不重入队；item-b 中断后重入队。
            Assert.Equal(1, reopened.Snapshot().Depth);

            var log = await ReadLogAsync(reopened);
            Assert.Equal(InputItemStatus.Canceled, log.Last(i => i.ItemId == "item-a").Status);

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

        // 状态仍由消费者落定。
        Assert.Equal("item-a", queue.Snapshot().RunningItemId);
        await queue.CompleteAsync("item-a", InputItemStatus.Canceled);
        Assert.Null(queue.Snapshot().RunningItemId);

        // 未知或已终态的项无法取消。
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
            var log = await ReadLogAsync(reopened);
            var last = log[^1];
            Assert.Equal("item-a", last.ItemId);
            Assert.Equal(InputItemStatus.Failed, last.Status);
            Assert.Equal("boom", last.Error);
            Assert.Equal(clock.GetUtcNow(), last.FinishedAt!.Value);
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

        // 模拟崩溃：文件末尾多出半行（无换行）。
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
                // 句柄未释放时留待系统清理，不掩盖测试结果。
            }
        }
    }
}
