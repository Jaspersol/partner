namespace Partner.Smoke;

internal static partial class Program
{
    private static async Task GameChecksAsync()
    {
        var gameDll = FindGameTestServer();
        if (gameDll is null)
        {
            Check("game: test server is built", false,
                "tests/Partner.GameTestServer/bin/*/net10.0/partner-game-testserver.dll not found — run dotnet build first");
        }
        else
        {
            await using var adapter = new ProcessGameAdapter(new ProcessGameAdapterOptions
            {
                GameId = "smoke-grid",
                Command = "dotnet",
                Arguments = new[] { gameDll },
                CallTimeout = TimeSpan.FromSeconds(10),
            });

            await adapter.AttachAsync();
            var initial = await adapter.ObserveAsync();
            Check("game: adapter attaches to a real game process and reads its state",
                adapter.IsAttached
                && initial.GetInt("hp") == 20
                && initial.GetInt("enemies") == 1
                && initial.GetInt("tick") == 0,
                $"hp={initial.GetInt("hp")} enemies={initial.GetInt("enemies")} tick={initial.GetInt("tick")}");

            // Reflex: a behavior that closes on the enemy, driven by the same engine the product uses.
            var engine = new ReflexEngine(adapter, new ReflexOptions { SelectMode = _ => "combat" });
            var attacks = 0;

            engine.Register(new Behavior("attack-nearby", async (api, ct) =>
            {
                var outcome = await api.Adapter.ActAsync(GameActionRequest.Of("attack"), ct).ConfigureAwait(false);
                if (outcome.Ok)
                {
                    attacks++;
                    api.Escalate(new RawSignal("game", SignalKind.Sensor, "enemy_killed", "1", DateTimeOffset.UtcNow));
                }
            }, score: ctx => ctx.Threat.Score > 0 ? 10 : 0));

            engine.Context.UpdateThreat(new ThreatState(1.0, DateTimeOffset.UtcNow, "enemy"));
            var ran = await engine.TickAsync();

            // A tick returns while the behavior is still running, so wait for it before asserting.
            await engine.WaitForIdleAsync();
            var after = await adapter.ObserveAsync();

            Check("game: a reflex behavior acts and the world changes",
                ran == "attack-nearby" && attacks == 1
                && after.GetInt("enemies") == 0 && after.GetInt("tick") == 1,
                $"ran={ran} attacks={attacks} enemies={after.GetInt("enemies")} tick={after.GetInt("tick")}");

            Check("game: the action outcome carries the post-action state",
                engine.Decisions.Count == 1 && engine.Gate.EscalationCount == 1,
                $"decisions={engine.Decisions.Count} escalations={engine.Gate.EscalationCount}");

            var latency = adapter.Latency;
            Console.WriteLine(
                $"  INFO  game latency: {latency.Samples} round trips, p50 {latency.P50Ms:0.0}ms, "
                + $"p99 {latency.P99Ms:0.0}ms, max {latency.MaxMs:0.0}ms");
            Check("game: latency is measured, not assumed",
                latency.Samples >= 3 && latency.P99Ms >= latency.P50Ms && latency.MaxMs > 0,
                $"samples={latency.Samples} p50={latency.P50Ms:0.0} p99={latency.P99Ms:0.0}");

            // An async behavior holds the slot: the next tick must not preempt it.
            var slotEngine = new ReflexEngine(adapter);
            var gate = new TaskCompletionSource();
            var preemptorRuns = 0;

            slotEngine.Register(new Behavior("slow", (_, ct) => gate.Task.WaitAsync(ct), score: _ => 100));
            slotEngine.Register(new Behavior("preemptor", (_, _) =>
            {
                preemptorRuns++;
                return Task.CompletedTask;
            }, score: _ => 99));

            await slotEngine.TickAsync();
            var blocked = await slotEngine.TickAsync();
            Check("game: an async reflex behavior is not preempted mid-run",
                blocked is null && preemptorRuns == 0 && slotEngine.SlotLocked,
                $"blocked={blocked} preemptorRuns={preemptorRuns} locked={slotEngine.SlotLocked}");

            gate.SetResult();
            await Task.Delay(50);

            // Sensor debounce: chatter collapses, but nothing on the action path is ever dropped.
            var hub = new SensorHub(TimeSpan.FromMilliseconds(200));
            var now = DateTimeOffset.UtcNow;
            hub.Emit(new RawSignal("game", SignalKind.Sensor, "position", "1,1", now));
            hub.Emit(new RawSignal("game", SignalKind.Sensor, "position", "1,2", now.AddMilliseconds(50)));
            hub.Emit(new RawSignal("game", SignalKind.ActionGuard, "pending_action", "1", now));
            hub.Emit(new RawSignal("game", SignalKind.PostCheck, "verify", "ok", now.AddMilliseconds(1)));

            Check("game: sensor chatter debounces while action signals never do",
                hub.Accepted.Count == 3 && hub.Dropped.Count == 1
                && hub.Accepted.Any(s => s.Kind == SignalKind.ActionGuard)
                && hub.Accepted.Any(s => s.Kind == SignalKind.PostCheck),
                $"accepted={hub.Accepted.Count} dropped={hub.Dropped.Count}");

            await adapter.DetachAsync();
            Check("game: detaching stops the child process", !adapter.IsAttached);
        }

    }
}
