using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The per-host gate in front of every request. Its whole job is that two requests to one host do not arrive as a
/// burst, because a burst is the shape of a scraper and a scraper is what the rate limits and the challenge pages
/// are there to stop.
/// <para>
/// The tests supply the clock and the wait rather than spending them, so what is checked is the gap that was asked
/// for and not the patience of the machine running the suite.
/// </para>
/// </summary>
public sealed class HostGateTests
{
    [Fact]
    public void Wait_ReturnsAtOnceForAHostItHasNotSeen()
    {
        // The ordinary case: the first request of a session, and every host the fanout only touches once.
        var clock = new Clock();
        var waits = new DelayLog();
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: waits.Wait);

        var first = gate.WaitAsync("example.com");
        var second = gate.WaitAsync("other.example");

        Assert.True(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.Empty(waits.Waits);
    }

    [Fact]
    public async Task Wait_GivesAHostItsGapBeforeTheNextRequestGoesOut()
    {
        var clock = new Clock();
        var waits = new DelayLog();
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: waits.Wait);

        await gate.WaitAsync("example.com");
        await gate.WaitAsync("example.com");

        // 250 ms with no jitter at all, which is what a random source pinned to the middle gives.
        var wait = Assert.Single(waits.Waits);
        Assert.InRange(wait, TimeSpan.FromMilliseconds(249), TimeSpan.FromMilliseconds(251));
    }

    [Fact]
    public async Task Wait_MovesTheGapByUpToAFifthEitherWay()
    {
        // Requests sent exactly the same distance apart are as regular as a metronome, and regularity is a signal
        // in itself. The gap varies; it does not collapse and it does not grow.
        var low = await GapAsync(() => 0.0);
        var middle = await GapAsync(() => 0.5);
        var high = await GapAsync(() => 1.0);

        Assert.InRange(low, TimeSpan.FromMilliseconds(199), TimeSpan.FromMilliseconds(201));
        Assert.InRange(middle, TimeSpan.FromMilliseconds(249), TimeSpan.FromMilliseconds(251));
        Assert.InRange(high, TimeSpan.FromMilliseconds(299), TimeSpan.FromMilliseconds(301));
    }

    [Fact]
    public async Task Wait_MeasuresTheGapFromTheRequestThatAlreadyStarted()
    {
        // Time already spent since the last request counts against the gap; the gate is quiet for 250 ms between
        // requests, not 250 ms on top of however long the last one took.
        var clock = new Clock();
        var waits = new DelayLog();
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: waits.Wait);

        await gate.WaitAsync("example.com");
        clock.Now = clock.Now.AddMilliseconds(100);
        await gate.WaitAsync("example.com");

        var wait = Assert.Single(waits.Waits);
        Assert.InRange(wait, TimeSpan.FromMilliseconds(149), TimeSpan.FromMilliseconds(151));
    }

    [Fact]
    public async Task Wait_SkipsTheGapWhenTheHostHasBeenQuietLongerThanIt()
    {
        var clock = new Clock();
        var waits = new DelayLog();
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: waits.Wait);

        await gate.WaitAsync("example.com");
        clock.Now = clock.Now.AddSeconds(30);

        var second = gate.WaitAsync("example.com");

        Assert.True(second.IsCompleted);
        Assert.Empty(waits.Waits);
    }

    [Fact]
    public async Task Wait_DoesNotHoldUpOneHostForAnother()
    {
        // Serialising by host, not by engine and not globally: a request to a second host has no reason to queue
        // behind the first one.
        var clock = new Clock();
        var waits = new DelayLog { Hold = true };
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: waits.Wait);

        await gate.WaitAsync("a.example");
        var blocked = gate.WaitAsync("a.example");
        var other = gate.WaitAsync("b.example");

        Assert.False(blocked.IsCompleted);
        Assert.True(other.IsCompleted);
        Assert.Single(waits.Waits);

        waits.Release();
        await blocked;
    }

    [Fact]
    public async Task Wait_CancelsWhileWaitingWithoutWedgingTheHost()
    {
        var clock = new Clock();
        var waits = new DelayLog { Hold = true };
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: waits.Wait);

        await gate.WaitAsync("a.example");

        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync("a.example", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // The turn was given up rather than used up, and a request ahead of this one failing is not this one's
        // problem: the next caller takes its own turn instead of inheriting a dead one.
        waits.Hold = false;
        await gate.WaitAsync("a.example");

        Assert.Equal(2, waits.Waits.Count);
    }

    [Fact]
    public async Task Wait_ForgetsFinishedHostsOnceTheTableHasGrownPastItsCeiling()
    {
        // The table is bounded so a session that touches many hosts does not grow one entry per host forever.
        var clock = new Clock();
        var gate = new HostGate(random: () => 0.5, clock: () => clock.Now, delay: (_, _) => Task.CompletedTask);

        await gate.WaitAsync("first.example");
        for (var index = 0; index < 130; index++)
        {
            await gate.WaitAsync($"host{index}.example");
        }

        // Ten milliseconds later: had the first host still been remembered, this would wait out the rest of its
        // gap. Being forgotten, it is simply a host this gate has not seen.
        clock.Now = clock.Now.AddMilliseconds(10);
        var again = gate.WaitAsync("first.example");

        Assert.True(again.IsCompleted);
    }

    [Fact]
    public void Wait_RejectsNothing()
    {
        var gate = new HostGate();

        Assert.ThrowsAny<ArgumentException>(() => { _ = gate.WaitAsync("  "); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = gate.WaitAsync(null!); });
    }

    /// <summary>The gap a second request to one host is asked to wait, with the random source pinned.</summary>
    private static async Task<TimeSpan> GapAsync(Func<double> random)
    {
        var clock = new Clock();
        var waits = new DelayLog();
        var gate = new HostGate(gap: null, random: random, clock: () => clock.Now, delay: waits.Wait);

        await gate.WaitAsync("example.com");
        await gate.WaitAsync("example.com");

        return Assert.Single(waits.Waits);
    }

    /// <summary>A clock the test moves by hand, so a gap is compared rather than spent.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Records the waits the gate asks for. With <see cref="Hold"/> set the next wait is handed back unfinished,
    /// which is what lets a test watch a request occupy its host's turn.
    /// </summary>
    private sealed class DelayLog
    {
        public List<TimeSpan> Waits { get; } = [];

        public bool Hold { get; set; }

        private TaskCompletionSource? Held { get; set; }

        public Task Wait(TimeSpan wait, CancellationToken ct)
        {
            Waits.Add(wait);
            if (!Hold)
            {
                return Task.CompletedTask;
            }

            Held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => Held.TrySetCanceled(ct));
            return Held.Task;
        }

        public void Release() => Held!.SetResult();
    }
}
