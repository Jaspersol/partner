using Partner.Game.Adapter;
using Partner.Game.Perception;
using Partner.Game.Reflex;

namespace Partner.Game.Tests.Reflex;

/// <summary>A behavior whose applicability, score and duration the test controls.</summary>
internal sealed class StubBehavior(
    string id,
    Func<ReflexContextState, double>? score = null,
    Func<ReflexApi, CancellationToken, Task>? run = null,
    IReadOnlyList<string>? modes = null,
    double cooldownMs = 0) : IReflexBehavior
{
    public int Runs { get; private set; }

    public string Id { get; } = id;

    public IReadOnlyList<string> Modes { get; } = modes ?? Array.Empty<string>();

    public double CooldownMs { get; } = cooldownMs;

    public bool When(ReflexContextState context, ReflexApi api) => true;

    public double Score(ReflexContextState context, ReflexApi api) => score?.Invoke(context) ?? 1;

    public Task RunAsync(ReflexApi api, CancellationToken ct)
    {
        Runs++;
        return run?.Invoke(api, ct) ?? Task.CompletedTask;
    }
}

/// <summary>Adapter double: the reflex tests never need a real game process.</summary>
internal sealed class StubAdapter : IGameAdapter
{
    public string GameId => "stub";

    public bool IsAttached { get; private set; }

    public LatencyStats Latency => new(0, 0, 0, 0);

    public Task AttachAsync(CancellationToken ct = default)
    {
        IsAttached = true;
        return Task.CompletedTask;
    }

    public Task DetachAsync(CancellationToken ct = default)
    {
        IsAttached = false;
        return Task.CompletedTask;
    }

    public Task<GameStateSnapshot> ObserveAsync(CancellationToken ct = default) =>
        Task.FromResult(new GameStateSnapshot
        {
            Seq = 1,
            At = DateTimeOffset.UnixEpoch,
            Values = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" },
        });

    public Task<GameActionOutcome> ActAsync(GameActionRequest action, CancellationToken ct = default) =>
        Task.FromResult(new GameActionOutcome { Ok = true, Detail = action.Kind, LatencyMs = 0 });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// The reflex engine. The slot lock is mandatory: an async behavior owns the body for its whole run.
/// </summary>
public sealed class ReflexEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static ReflexEngine Engine(TestClock clock, ReflexOptions? options = null) =>
        new(new StubAdapter(), options, clock);

    [Fact]
    public async Task Highest_score_wins_and_synchronous_behaviors_free_the_slot()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        var weak = new StubBehavior("weak", _ => 1);
        var strong = new StubBehavior("strong", _ => 5);
        engine.Register(weak);
        engine.Register(strong);

        var ran = await engine.TickAsync();

        Assert.Equal("strong", ran);
        Assert.Equal(1, strong.Runs);
        Assert.Equal(0, weak.Runs);
        Assert.False(engine.SlotLocked);
    }

    [Fact]
    public async Task Zero_score_behaviors_never_run()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        engine.Register(new StubBehavior("no", _ => 0));

        Assert.Null(await engine.TickAsync());
    }

    [Fact]
    public async Task Cooldown_prevents_an_immediate_rerun()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        var behavior = new StubBehavior("tick", cooldownMs: 1_000);
        engine.Register(behavior);

        Assert.Equal("tick", await engine.TickAsync());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Null(await engine.TickAsync());

        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Equal("tick", await engine.TickAsync());
        Assert.Equal(2, behavior.Runs);
    }

    [Fact]
    public async Task Mode_filter_excludes_behaviors_that_do_not_apply()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        var workOnly = new StubBehavior("work", modes: new[] { "work" });
        var anyMode = new StubBehavior("any", _ => 0.5);
        engine.Register(workOnly);
        engine.Register(anyMode);

        engine.TransitionMode("idle");
        Assert.Equal("any", await engine.TickAsync());

        engine.TransitionMode("work");
        Assert.Equal("work", await engine.TickAsync());
    }

    [Fact]
    public async Task Async_behavior_holds_the_slot_for_its_whole_run()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        var released = new TaskCompletionSource();

        // A 1.5 s survival action: with a short policy cap the next tick would preempt it mid-way.
        var slow = new StubBehavior("survive", score: _ => 100, run: async (_, ct) =>
        {
            await released.Task.WaitAsync(ct);
        });
        var boostPreemptor = false;
        var preemptor = new StubBehavior("preemptor", _ => boostPreemptor ? 200 : 99);
        engine.Register(slow);
        engine.Register(preemptor);

        var first = engine.TickAsync();
        Assert.Equal("survive", await first);

        // While the slow behavior runs, later ticks must do nothing at all.
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.Null(await engine.TickAsync());
        }

        Assert.True(engine.SlotLocked);
        Assert.Equal(0, preemptor.Runs);

        released.SetResult();
        await Task.Delay(50);

        // The slot is free again, so selection resumes — and now the higher score wins.
        Assert.False(engine.SlotLocked);
        boostPreemptor = true;
        Assert.Equal("preemptor", await engine.TickAsync());
    }

    [Fact]
    public async Task The_slot_lock_is_bounded_only_by_the_deadlock_breaker()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock, new ReflexOptions { SlotLockDeadlockMs = 500 });

        engine.Register(new StubBehavior("stuck", run: (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default)));

        Assert.Equal("stuck", await engine.TickAsync());

        // Past the deadlock breaker the slot is considered free again rather than wedged forever.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(engine.SlotLocked);
    }

    [Fact]
    public async Task Decisions_are_audited_with_score_and_lock_state()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        engine.Register(new StubBehavior("sync", _ => 3));

        await engine.TickAsync();

        var decision = Assert.Single(engine.Decisions);
        Assert.Equal("sync", decision.BehaviorId);
        Assert.Equal(3, decision.Score);
        Assert.False(decision.LockedAsync);
    }

    [Fact]
    public async Task Select_mode_computes_the_mode_from_context_unless_pinned()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock, new ReflexOptions { SelectMode = ctx => ctx.Threat.Score > 0.5 ? "alert" : "idle" });
        engine.Register(new StubBehavior("alert-behavior", modes: new[] { "alert" }));

        Assert.Null(await engine.TickAsync());

        engine.Context.UpdateThreat(new ThreatState(0.9, T0, "mob"));
        Assert.Equal("alert-behavior", await engine.TickAsync());

        // Pinned modes are not overridden by the selector.
        engine.TransitionMode("work");
        Assert.Equal("work", engine.Mode);
        Assert.Null(await engine.TickAsync());
    }

    [Fact]
    public void Registering_the_same_id_twice_is_rejected()
    {
        var engine = Engine(new TestClock(T0));
        engine.Register(new StubBehavior("dup"));

        Assert.Throws<InvalidOperationException>(() => engine.Register(new StubBehavior("dup")));
    }

    [Fact]
    public async Task A_failing_behavior_releases_the_slot_and_records_the_error()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        engine.Register(new StubBehavior("boom", run: (_, _) => throw new InvalidOperationException("kaboom")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.TickAsync());

        Assert.False(engine.SlotLocked);
        Assert.Null(engine.ActiveBehaviorId);
        Assert.Equal("kaboom", engine.Context.GetSnapshot().Autonomy.LastError);
    }

    [Fact]
    public async Task Only_escalated_signals_pass_the_conscious_gate()
    {
        var clock = new TestClock(T0);
        var engine = Engine(clock);
        var signal = new RawSignal("sensor", SignalKind.Sensor, "enemy_near", "1", T0);

        Assert.False(engine.Gate.ShouldForward(signal));

        engine.Register(new StubBehavior("escalate", run: (api, _) =>
        {
            api.Escalate(signal);
            return Task.CompletedTask;
        }));

        await engine.TickAsync();

        Assert.True(engine.Gate.ShouldForward(signal));
        Assert.Equal(1, engine.Gate.EscalationCount);
    }
}

/// <summary>Perception debounce rules: sensors debounce, action paths never do.</summary>
public sealed class SensorHubTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static RawSignal Signal(SignalKind kind, string name = "x", double atMs = 0) =>
        new("sensor-1", kind, name, "1", T0.AddMilliseconds(atMs));

    [Fact]
    public void Sensor_signals_are_debounced_within_the_window()
    {
        var hub = new SensorHub(TimeSpan.FromMilliseconds(200));

        // Trailing debounce: every signal — including dropped ones — restarts the window, so a continuous
        // stream is only let through once it pauses.
        Assert.True(hub.Emit(Signal(SignalKind.Sensor, atMs: 0)));
        Assert.False(hub.Emit(Signal(SignalKind.Sensor, atMs: 100)));
        Assert.False(hub.Emit(Signal(SignalKind.Sensor, atMs: 250)));
        Assert.True(hub.Emit(Signal(SignalKind.Sensor, atMs: 500)));

        Assert.Equal(2, hub.Accepted.Count);
        Assert.Equal(2, hub.Dropped.Count);
    }

    [Fact]
    public void The_window_is_trailing_so_a_burst_collapses_to_its_end()
    {
        var hub = new SensorHub(TimeSpan.FromMilliseconds(200));

        hub.Emit(Signal(SignalKind.Sensor, atMs: 0));
        hub.Emit(Signal(SignalKind.Sensor, atMs: 150)); // resets the window
        Assert.False(hub.Emit(Signal(SignalKind.Sensor, atMs: 300)));

        Assert.Single(hub.Accepted);
        Assert.Equal(2, hub.Dropped.Count);
    }

    [Fact]
    public void Action_guard_post_check_and_manual_signals_are_never_debounced()
    {
        var hub = new SensorHub(TimeSpan.FromMilliseconds(1_000));

        foreach (var kind in new[] { SignalKind.ActionGuard, SignalKind.PostCheck, SignalKind.Manual })
        {
            Assert.True(hub.Emit(Signal(kind, atMs: 0)));
            Assert.True(hub.Emit(Signal(kind, atMs: 1)));
            Assert.True(hub.Emit(Signal(kind, atMs: 2)));
        }

        Assert.Equal(9, hub.Accepted.Count);
        Assert.Empty(hub.Dropped);
    }

    [Fact]
    public void Debounce_is_per_source_and_per_name()
    {
        var hub = new SensorHub(TimeSpan.FromMilliseconds(200));

        Assert.True(hub.Emit(new RawSignal("a", SignalKind.Sensor, "x", "1", T0)));
        Assert.True(hub.Emit(new RawSignal("b", SignalKind.Sensor, "x", "1", T0)));
        Assert.True(hub.Emit(new RawSignal("a", SignalKind.Sensor, "y", "1", T0)));
        Assert.False(hub.Emit(new RawSignal("a", SignalKind.Sensor, "x", "2", T0.AddMilliseconds(50))));
    }

    [Fact]
    public void A_zero_debounce_window_accepts_everything()
    {
        var hub = new SensorHub(TimeSpan.Zero);

        Assert.True(hub.Emit(Signal(SignalKind.Sensor, atMs: 0)));
        Assert.True(hub.Emit(Signal(SignalKind.Sensor, atMs: 1)));
        Assert.Empty(hub.Dropped);
    }

    [Fact]
    public void Signals_without_a_timestamp_get_the_clock_time()
    {
        var clock = new TestClock(T0);
        var hub = new SensorHub(TimeSpan.FromMilliseconds(200), clock);

        Assert.True(hub.Emit(new RawSignal("s", SignalKind.Sensor, "n", "v", default)));
        Assert.Equal(T0, hub.Accepted[0].At);
    }
}
