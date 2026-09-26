using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Governance;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Mcp;
using Partner.Core.Agent.Skills;
using Partner.Core.Agent.Tools;
using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Manifest;
using Partner.Core.Memory;
using Partner.Desktop;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;
using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Perception;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Tts;
using Partner.Game.Adapter;
using Partner.Game.Perception;
using Partner.Game.Reflex;
using Partner.Api;
using Partner.Api.Auth;
using Partner.Api.Endpoints;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Partner.Core.Queue;
using Partner.Core.Scheduler;
using Partner.Core.Sessions;

namespace Partner.Smoke;

/// <summary>
/// Phase 1 smoke harness: runs the real end-to-end path (three input kinds → one FIFO queue →
/// single consumer → one session log → projection), including a restart, and asserts the
/// invariants the design doc requires. Exits non-zero on the first failed check.
/// <para>
/// This is the "smoke 实跑" source that the capability manifest is generated from (doc §8.5 F):
/// nothing here reads configuration to decide whether something works.
/// </para>
/// </summary>
internal static class Program
{
    private static readonly List<(string Name, bool Passed, string? Detail)> Results = new();
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        var options = SmokeOptions.Parse(args);
        var root = Path.Combine(Path.GetTempPath(), "partner-smoke", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        Console.WriteLine($"workspace: {root}");

        try
        {
            await RunAsync(root);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UNHANDLED: {ex}");
            _failures++;
        }

        var manifest = BuildManifest();
        WriteJson(options.ManifestPath, manifest);
        Console.WriteLine($"manifest: {options.ManifestPath}");

        var gateOk = true;

        if (options.UpdateBaselinePath is not null)
        {
            WriteJson(options.UpdateBaselinePath, manifest);
            Console.WriteLine($"baseline updated: {options.UpdateBaselinePath}");
        }

        if (options.VerifyBaselinePath is not null)
        {
            gateOk = VerifyBaseline(options.VerifyBaselinePath, manifest);
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({_failures} check(s))");

        // A failed run keeps its workspace so the state can be inspected (the path is printed above); a
        // passing run must not leak ~25 MB per invocation into the temp directory.
        if (_failures == 0)
        {
            TryDeleteWorkspace(root);
        }
        else
        {
            Console.WriteLine($"workspace kept for inspection: {root}");
        }

        if (!gateOk)
        {
            return 1;
        }

        return _failures == 0 ? 0 : 1;
    }

    private static void TryDeleteWorkspace(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException ex)
        {
            // Best effort: a leftover workspace is not worth failing an otherwise green run over.
            Console.WriteLine($"workspace left behind ({ex.GetType().Name}): {root}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"workspace left behind ({ex.GetType().Name}): {root}");
        }
    }

    /// <summary>Fails the build when a capability disappears or drops a level (design doc Phase 0).</summary>
    private static bool VerifyBaseline(string path, CapabilityManifest current)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"GATE FAILED: baseline '{path}' not found — create it with --update-baseline <path>");
            return false;
        }

        CapabilityManifest? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<CapabilityManifest>(File.ReadAllText(path), CapabilityManifest.Json);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"GATE FAILED: baseline '{path}' is not readable: {ex.Message}");
            return false;
        }

        if (baseline is null)
        {
            Console.Error.WriteLine($"GATE FAILED: baseline '{path}' is empty");
            return false;
        }

        var report = CapabilityManifest.Compare(baseline, current);

        foreach (var line in report.Improvements)
        {
            Console.WriteLine($"  GATE  {line}");
        }

        foreach (var line in report.Added)
        {
            Console.WriteLine($"  GATE  {line} (update the baseline to lock it in)");
        }

        if (report.Ok)
        {
            Console.WriteLine($"  GATE  PASS  no capability regressed ({current.Capabilities.Count} capabilities)");
            return true;
        }

        foreach (var line in report.Regressions)
        {
            Console.Error.WriteLine($"  GATE  FAIL  {line}");
        }

        return false;
    }

    private sealed record SmokeOptions(
        string ManifestPath,
        string? VerifyBaselinePath,
        string? UpdateBaselinePath)
    {
        public static SmokeOptions Parse(string[] args)
        {
            var manifest = Path.Combine(Directory.GetCurrentDirectory(), "capabilities", "manifest.json");
            string? verify = null;
            string? update = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--manifest" when i + 1 < args.Length:
                        manifest = args[++i];
                        break;
                    case "--verify-baseline" when i + 1 < args.Length:
                        verify = args[++i];
                        break;
                    case "--update-baseline" when i + 1 < args.Length:
                        update = args[++i];
                        break;
                    default:
                        // Backwards-compatible positional form: a single path is the manifest path.
                        if (!args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            manifest = args[i];
                        }

                        break;
                }
            }

            return new SmokeOptions(manifest, verify, update);
        }
    }

    private static async Task RunAsync(string root)
    {
        var clock = TimeProvider.System;

        // ---- 1. three input kinds, interleaved arrival order -------------------------
        var (store, repaired) = JsonlSessionStore.Open(root, clock);
        Check("session log opens clean", repaired == 0, $"repaired={repaired}");

        var queue = new JsonlInputQueue(root, capacity: 8, clock: clock);

        var arrival = new (InputSource Source, string Payload, string Key)[]
        {
            (InputSource.Voice, "turn on the lamp", "voice:dev1:bootA:1200"),
            (InputSource.Text, "and the fan", "text:client1:msg-1"),
            (InputSource.Schedule, "{\"task\":\"daily-digest\"}", "schedule:daily-digest:2026-09-26T09:00Z"),
            (InputSource.Text, "duplicate of msg-1", "text:client1:msg-1"),
        };

        var seqs = new List<long>();
        foreach (var a in arrival)
        {
            seqs.Add(await queue.EnqueueAsync(new InputItem
            {
                ItemId = Guid.NewGuid().ToString("n"),
                Source = a.Source,
                Payload = a.Payload,
                IdempotencyKey = a.Key,
            }));
        }

        Check("duplicate idempotency key reuses the seq", seqs[3] == seqs[1], $"got {seqs[3]} vs {seqs[1]}");
        Check("queue depth counts the three distinct items", queue.Snapshot().Depth == 3,
            $"depth={queue.Snapshot().Depth}");

        // ---- 2. single consumer: take → run → write session → complete ---------------
        var consumed = new List<InputItem>();
        for (var i = 0; i < 3; i++)
        {
            var item = await queue.TakeAsync();
            Check($"take #{i + 1} returns an item", item is not null);
            if (item is null)
            {
                break;
            }

            consumed.Add(item);
            Check($"running item is visible while executing", queue.Snapshot().RunningItemId == item.ItemId,
                $"running={queue.Snapshot().RunningItemId}");

            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = item.Source,
                Body = item.Payload,
                Meta = new Dictionary<string, string> { ["itemId"] = item.ItemId, ["seq"] = item.Seq.ToString() },
            });

            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Assistant,
                Source = item.Source,
                Body = $"ack({item.Payload})",
            });

            await queue.CompleteAsync(item.ItemId, InputItemStatus.Done);
        }

        Check("consumed in arrival order",
            consumed.Select(c => c.Payload).SequenceEqual(new[]
            {
                "turn on the lamp", "and the fan", "{\"task\":\"daily-digest\"}",
            }),
            string.Join(" | ", consumed.Select(c => c.Payload)));

        Check("queue drained", queue.Snapshot().Depth == 0, $"depth={queue.Snapshot().Depth}");
        Check("no running item after drain", queue.Snapshot().RunningItemId is null);

        // ---- 3. projection: order, edits, rebuildability -----------------------------
        var view = await store.ProjectAsync();
        Check("session has 6 content events (3 user + 3 assistant)", view.Items.Count == 6,
            $"count={view.Items.Count}");
        Check("projection preserves arrival order",
            view.Items.Where(i => i.Kind == SessionEventKind.User).Select(i => i.Body)
                .SequenceEqual(new[] { "turn on the lamp", "and the fan", "{\"task\":\"daily-digest\"}" }));

        // Edit the first user turn: the view must show the new text, keep the original recoverable.
        var firstSeq = view.Items[0].Seq;
        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Edit,
            Source = InputSource.Text,
            Body = "turn on the desk lamp",
            EditedFromSeq = firstSeq,
        });

        var edited = await store.ProjectAsync();
        var editedItem = edited.Items.Single(i => i.Seq == firstSeq);
        Check("edit replaces the projected body", editedItem.Body == "turn on the desk lamp", editedItem.Body);
        Check("edit keeps the original body", editedItem.OriginalBody == "turn on the lamp",
            editedItem.OriginalBody ?? "(null)");
        Check("edit is not rendered as its own content", edited.Items.All(i => i.Kind != SessionEventKind.Edit));
        Check("revision count is 1", editedItem.RevisionCount == 1, $"count={editedItem.RevisionCount}");

        var rawEvents = 0;
        await foreach (var _ in store.ReadAllAsync())
        {
            rawEvents++;
        }

        Check("raw log keeps every event (edits included)", rawEvents == 7, $"raw={rawEvents}");

        // Rebuild the projection from the raw log: must match the incremental result.
        var rebuilt = new List<SessionEvent>();
        await foreach (var e in store.ReadAllAsync())
        {
            rebuilt.Add(e);
        }

        var rebuiltView = SessionProjection.Build(rebuilt);
        Check("projection is rebuildable from the log",
            rebuiltView.Items.Select(i => (i.Seq, i.Body)).SequenceEqual(edited.Items.Select(i => (i.Seq, i.Body))));

        await store.DisposeAsync();

        // ---- 4. restart: unconsumed items come back, consumed stay consumed ----------
        // A real restart means the previous process is gone: release its handles first.
        await queue.DisposeAsync();

        var queue2 = new JsonlInputQueue(root, capacity: 8, clock: clock);
        Check("restart: nothing queued yet", queue2.Snapshot().Depth == 0, $"depth={queue2.Snapshot().Depth}");

        var recoveredSeq = await queue2.EnqueueAsync(new InputItem
        {
            ItemId = Guid.NewGuid().ToString("n"),
            Source = InputSource.Text,
            Payload = "duplicate of msg-1",
            IdempotencyKey = "text:client1:msg-1",
        });
        Check("restart: consumed idempotency key still dedupes", recoveredSeq == seqs[1],
            $"got {recoveredSeq} vs {seqs[1]}");

        var newSeq = await queue2.EnqueueAsync(new InputItem
        {
            ItemId = Guid.NewGuid().ToString("n"),
            Source = InputSource.Text,
            Payload = "after restart",
            IdempotencyKey = "text:client1:msg-2",
        });
        Check("restart: new item gets a fresh seq", newSeq > seqs.Max(), $"new={newSeq} max={seqs.Max()}");

        var afterRestart = await queue2.TakeAsync();
        Check("restart: the new item is taken", afterRestart?.Payload == "after restart",
            afterRestart?.Payload ?? "(null)");
        if (afterRestart is not null)
        {
            // Simulate a crash mid-run: do not complete it, just dispose.
            await queue2.DisposeAsync();

            var queue3 = new JsonlInputQueue(root, capacity: 8, clock: clock);
            var resumed = await queue3.TakeAsync();
            Check("crash recovery: interrupted item is re-queued", resumed?.ItemId == afterRestart.ItemId,
                $"{resumed?.ItemId} vs {afterRestart.ItemId}");
            Check("crash recovery: attempt counter advanced", resumed?.Attempt == 1,
                $"attempt={resumed?.Attempt}");
            if (resumed is not null)
            {
                await queue3.CompleteAsync(resumed.ItemId, InputItemStatus.Done);
            }

            await queue3.DisposeAsync();
        }

        // ---- 5. session survives restart with the same seq space ---------------------
        var (store2, repaired2) = JsonlSessionStore.Open(root, clock);
        Check("restart: session log still clean", repaired2 == 0, $"repaired={repaired2}");
        Check("restart: last seq preserved", store2.LastSeq == 7, $"lastSeq={store2.LastSeq}");

        var appended = await store2.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = "after restart",
        });
        Check("restart: seq continues without reuse", appended == 8, $"seq={appended}");
        await store2.DisposeAsync();

        // ---- 6. M3: a real turn through the agent runtime ---------------------------
        var (store3, _) = JsonlSessionStore.Open(root, clock);
        await using (store3)
        {
            var queue4 = new JsonlInputQueue(root, capacity: 8, clock: clock);

            // Skills: one valid skill, one invalid (no front matter) so the warning path is exercised too.
            WriteSkill(root, "smoke-skill", "Smoke Skill", "Proves skills load from the workspace", "Do the thing.");
            WriteSkill(root, "broken-skill", null, null, "no front matter here");
            var skills = new SkillLoader().Load(root);
            var skillRegistry = new InMemorySkillRegistry();
            skillRegistry.Reload(skills.Skills);
            Check("skills: workspace scan finds valid skills and warns about bad ones",
                skills.Skills.Count == 1 && skills.Skills[0].SkillId == "smoke-skill" && skills.Warnings.Count == 1,
                $"skills={skills.Skills.Count} warnings={skills.Warnings.Count}");

            var itemSeq = await queue4.EnqueueAsync(new InputItem
            {
                ItemId = Guid.NewGuid().ToString("n"),
                Source = InputSource.Text,
                Payload = "what time is it",
                IdempotencyKey = "text:client1:msg-3",
            });
            var turnItem = await queue4.TakeAsync();
            Check("turn: item is claimed before the turn", turnItem?.Seq == itemSeq,
                $"{turnItem?.Seq} vs {itemSeq}");

            var tools = new InMemoryToolRegistry();
            tools.Register(new StubTool("clock", "12:34"));
            var llm = new ScriptedLlmClient(new[]
            {
                new LlmResponse(null, new[] { new LlmToolCall("c1", "clock", "{}") }, "tool_calls"),
                new LlmResponse("it is 12:34", Array.Empty<LlmToolCall>(), "stop"),
            });
            var hooks = new HookRegistry();
            hooks.Register(new InjectingHook());
            hooks.Register(new SkillInjectionHook(skillRegistry));
            var runner = new AgentTurnRunner(
                store3,
                llm,
                tools,
                new AllowListGovernance(new[] { "clock" }),
                new DenyAllApprovalHandler(),
                hooks);

            var turn = await runner.RunAsync(turnItem!);
            Check("turn: completes with reason=completed", turn.Reason == "completed", turn.Reason);
            Check("turn: two steps (tool then answer)", turn.Steps == 2, $"steps={turn.Steps}");
            Check("turn: one tool call executed", turn.ToolCalls == 1, $"toolCalls={turn.ToolCalls}");

            var kinds = new List<SessionEventKind>();
            await foreach (var e in store3.ReadAsync(turn.TurnStartSeq, 100))
            {
                kinds.Add(e.Kind);
            }

            Check("turn: event order matches the spec (assistant only when the step has text)",
                kinds.SequenceEqual(new[]
                {
                    SessionEventKind.TurnStart,
                    SessionEventKind.StepStart,
                    SessionEventKind.User,
                    SessionEventKind.System,
                    SessionEventKind.ToolCall,
                    SessionEventKind.ToolResult,
                    SessionEventKind.StepEnd,
                    SessionEventKind.StepStart,
                    SessionEventKind.Assistant,
                    SessionEventKind.StepEnd,
                    SessionEventKind.TurnEnd,
                }),
                string.Join(",", kinds));

            var turnView = await store3.ProjectAsync();
            Check("turn: projection shows the answer but not control events",
                turnView.Items.Any(i => i.Body == "it is 12:34")
                && turnView.Items.All(i => !i.Kind.IsControl()),
                $"items={turnView.Items.Count}");
            Check("turn: hook injection is recorded as a system event",
                turnView.Items.Any(i => i.Kind == SessionEventKind.System && i.Body.Contains("brief")));

            Check("skills: injection reaches the model prompt",
                llm.Requests[0].Messages[0].Content!.Contains("smoke-skill", StringComparison.Ordinal)
                && llm.Requests[0].Messages[0].Content!.Contains("Available skills", StringComparison.Ordinal),
                llm.Requests[0].Messages[0].Content ?? "(null)");

            Check("skills: injection is recorded in the session",
                turnView.Items.Any(i => i.Kind == SessionEventKind.System && i.Body.Contains("smoke-skill")));

            await queue4.CompleteAsync(turnItem!.ItemId, InputItemStatus.Done);
            await queue4.DisposeAsync();
        }

        // ---- 7. M4: scheduler → queue → turn ---------------------------------------
        var (store4, _) = JsonlSessionStore.Open(root, clock);
        await using (store4)
        {
            var queue5 = new JsonlInputQueue(root, capacity: 8, clock: clock);
            var occurrence = clock.GetUtcNow();

            await using (var scheduler = new JsonlScheduler(root, queue5, clock))
            {
                await scheduler.ScheduleAsync(new ScheduleTask
                {
                    TaskId = "smoke-tick",
                    Trigger = new IntervalTrigger(TimeSpan.FromMinutes(5)),
                    Payload = "scheduled tick",
                    NextScheduledAt = occurrence,
                });

                var outcome = await scheduler.DeliverDueAsync();
                Check("scheduler: task delivers one queue item", outcome.Delivered == 1 && outcome.Failed == 0,
                    $"delivered={outcome.Delivered} failed={outcome.Failed}");

                var scheduled = await queue5.TakeAsync();
                Check("scheduler: item carries the (taskId, occurrenceAt) key",
                    scheduled?.Source == InputSource.Schedule
                    && scheduled.IdempotencyKey == $"schedule:smoke-tick:{occurrence.UtcDateTime:O}",
                    $"{scheduled?.Source} / {scheduled?.IdempotencyKey}");

                var task = (await scheduler.ListAsync()).Single();
                Check("scheduler: task advances to the next occurrence",
                    task.NextScheduledAt == occurrence.AddMinutes(5) && task.LastDeliveryAt == occurrence,
                    $"next={task.NextScheduledAt:O}");

                if (scheduled is not null)
                {
                    await queue5.CompleteAsync(scheduled.ItemId, InputItemStatus.Done);
                }
            }

            // Restart: the receipt must prevent a second delivery of the same occurrence.
            await using (var restarted = new JsonlScheduler(root, queue5, clock))
            {
                var replay = await restarted.DeliverDueAsync();
                Check("scheduler: restart does not re-deliver the same occurrence",
                    replay.Delivered == 0 && replay.SkippedAlreadyDelivered == 0,
                    $"delivered={replay.Delivered} skipped={replay.SkippedAlreadyDelivered}");
            }

            // A scheduled item runs through the same turn path as any other input.
            await using (var failing = new JsonlScheduler(root, new FailOnceQueue(queue5), clock))
            {
                await failing.ScheduleAsync(new ScheduleTask
                {
                    TaskId = "smoke-flaky",
                    Trigger = new IntervalTrigger(TimeSpan.FromMinutes(5)),
                    Payload = "flaky tick",
                    NextScheduledAt = occurrence,
                });

                var failedPass = await failing.DeliverDueAsync();
                var flaky = (await failing.ListAsync()).Single(t => t.TaskId == "smoke-flaky");
                Check("scheduler: failed delivery keeps the obligation",
                    failedPass.Failed == 1 && flaky.NextScheduledAt == occurrence && flaky.Attempt == 1
                    && flaky.LastError is not null,
                    $"failed={failedPass.Failed} attempt={flaky.Attempt} next={flaky.NextScheduledAt:O}");

                var recovered = await failing.DeliverDueAsync();
                Check("scheduler: a failing task is not lost and recovers",
                    recovered.Delivered == 1,
                    $"delivered={recovered.Delivered}");
            }

            var scheduledItem = await queue5.TakeAsync();
            Check("scheduler: scheduled item is claimable", scheduledItem is not null);

            if (scheduledItem is not null)
            {
                var runner = new AgentTurnRunner(
                    store4,
                    new ScriptedLlmClient(new[] { new LlmResponse("tick acknowledged", Array.Empty<LlmToolCall>(), "stop") }),
                    new InMemoryToolRegistry(),
                    new AllowListGovernance(Array.Empty<string>()),
                    new DenyAllApprovalHandler(),
                    new HookRegistry());

                var scheduledTurn = await runner.RunAsync(scheduledItem);
                Check("scheduler: scheduled item runs as a turn",
                    scheduledTurn.Reason == "completed" && scheduledTurn.Steps == 1,
                    $"{scheduledTurn.Reason}/{scheduledTurn.Steps}");

                var scheduleView = await store4.ProjectAsync();
                Check("scheduler: session records the schedule turn",
                    scheduleView.Items.Any(i => i.Source == InputSource.Schedule && i.Body == scheduledItem.Payload)
                    && scheduleView.Items.Any(i => i.Body == "tick acknowledged"),
                    $"payload={scheduledItem.Payload}");

                await queue5.CompleteAsync(scheduledItem.ItemId, InputItemStatus.Done);
            }

            await queue5.DisposeAsync();
        }

        // ---- 8. M5: memory layer ----------------------------------------------------
        await using (var memory = new MemoryStore(Path.Combine(root, "memory", "memory.db"), clock: clock))
        {
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "pref-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Global,
                Body = "the user prefers dark mode in every editor",
                Importance = 0.9,
            });
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "noise-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Global,
                Body = "the cat sleeps on the keyboard",
            });

            var hits = await memory.RecallAsync("dark mode editor");
            Check("memory: recall finds the relevant record and not the noise",
                hits.Count > 0 && hits[0].RecordId == "pref-1" && hits.All(h => h.RecordId != "noise-1"),
                string.Join(",", hits.Select(h => h.RecordId)));

            Check("memory: recall is explainable",
                hits.Count > 0 && hits[0].Reasons.Count > 0
                && hits[0].Reasons.Any(r => r.StartsWith("lexical", StringComparison.Ordinal)),
                string.Join(" | ", hits.Count > 0 ? hits[0].Reasons : Array.Empty<string>()));

            // Offline path: the default store has no embedding provider at all, and recall still works.
            Check("memory: offline (no embeddings) still recalls", hits.Count > 0);

            // Privacy delete: four steps, then compaction removes the row entirely.
            await memory.SoftDeleteAsync("pref-1", Actor.Human);
            var purged = await memory.GetAsync("pref-1");
            var afterDelete = await memory.RecallAsync("dark mode editor");
            var compacted = await memory.CompactAsync();
            Check("memory: privacy delete purges, hides from search, then compacts",
                purged is not null && purged.Body.Length == 0 && purged.ValidTo is not null
                && afterDelete.All(h => h.RecordId != "pref-1")
                && compacted == 1 && await memory.GetAsync("pref-1") is null,
                $"compacted={compacted}");

            // Promotion preserves content as an episodic copy.
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "working-1",
                Tier = MemoryTier.Working,
                Scope = MemoryScope.Recall,
                Body = "scratch note about the deployment window",
                CreatedAt = clock.GetUtcNow() - TimeSpan.FromHours(30),
            });
            var promoted = await memory.PromoteEligibleAsync();
            var copy = await memory.GetAsync(MemoryStore.EpisodicCopyId("working-1"));
            Check("memory: promotion preserves content as an episodic copy",
                promoted == 1 && copy is not null && copy.Tier == MemoryTier.Episodic
                && copy.Body == "scratch note about the deployment window",
                $"promoted={promoted}");

            // Injection budget.
            await using (var tight = new MemoryStore(
                Path.Combine(root, "memory", "tight.db"),
                new MemoryStoreOptions { InjectionTokenLimit = 20, RecallLimit = 10 },
                clock))
            {
                for (var i = 0; i < 5; i++)
                {
                    await tight.WriteAsync(new MemoryRecord
                    {
                        RecordId = $"budget-{i}",
                        Tier = MemoryTier.Semantic,
                        Scope = MemoryScope.Global,
                        Body = "alpha beta gamma delta epsilon zeta eta theta iota kappa",
                    });
                }

                var injection = await tight.InjectAsync("alpha beta");
                Check("memory: injection respects the token budget",
                    injection.Tokens <= 20 && injection.Truncated && injection.Hits.Count < 5,
                    $"tokens={injection.Tokens} hits={injection.Hits.Count} truncated={injection.Truncated}");
            }

            // Scale: the Phase 3 acceptance number (100k records, retrieval P99 < 500ms).
            const int scale = 100_000;
            var records = new List<MemoryRecord>(scale);
            for (var i = 0; i < scale; i++)
            {
                records.Add(new MemoryRecord
                {
                    RecordId = $"bulk-{i}",
                    Tier = MemoryTier.Semantic,
                    Scope = MemoryScope.Global,
                    Body = $"record {i} about deployment pipelines and blue green releases in region {i % 16}",
                });
            }

            var seedWatch = System.Diagnostics.Stopwatch.StartNew();
            await memory.WriteBatchAsync(records);
            seedWatch.Stop();

            var queries = new[]
            {
                "deployment pipelines", "blue green releases", "region 7", "record 9999", "pipelines region 3",
            };

            var samples = new List<double>();
            for (var i = 0; i < 60; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await memory.RecallAsync(queries[i % queries.Length]);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            samples.Sort();
            var p99 = samples[(int)Math.Ceiling(samples.Count * 0.99) - 1];
            Console.WriteLine(
                $"  INFO  memory scale: {scale} records, seed {seedWatch.Elapsed.TotalSeconds:0.0}s, "
                + $"recall median {samples[samples.Count / 2]:0.0}ms p99 {p99:0.0}ms");
            Check($"memory: {scale} records — retrieval P99 under 500ms",
                p99 < 500,
                $"p99={p99:0.0}ms median={samples[samples.Count / 2]:0.0}ms seed={seedWatch.Elapsed.TotalSeconds:0.0}s");
        }

        // ---- 9. M6: tool governance -------------------------------------------------
        var policyPath = Path.Combine(root, "tools.policy.json");
        File.WriteAllText(policyPath, """
            {
              "version": 1,
              "tools": {
                "clock":      { "decision": "allow" },
                "rm.rf":      { "decision": "deny" },
                "file.write": { "decision": "allow", "rules": { "allowedPathPrefixes": ["${workspace}"] } }
              }
            }
            """);

        var policy = ToolPolicy.Load(policyPath, root);
        var governance = new PolicyToolGovernance(policy);

        Check("governance: policy allows a listed tool",
            governance.Decide("clock", "{}").Decision == ToolDecision.Allow);

        Check("governance: explicit deny is refused",
            governance.Decide("rm.rf", "{}").Decision == ToolDecision.Deny);

        Check("governance: unlisted tool is refused (fail-closed)",
            governance.Decide("never-registered", "{}").Decision == ToolDecision.Deny
            && governance.Decide("never-registered", "{}").Reason.Contains("fail-closed", StringComparison.Ordinal));

        // Build the arguments as real JSON: Windows paths must be escaped, not hand-concatenated.
        var insidePath = Path.Combine(root, "notes.txt");
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside.txt");
        var insideArgs = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = insidePath });
        var outsideArgs = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = outsidePath });

        Check("governance: target parameter rules reject an out-of-scope target",
            governance.Decide("file.write", insideArgs).Decision == ToolDecision.Allow
            && governance.Decide("file.write", outsideArgs).Decision == ToolDecision.Deny,
            $"inside={governance.Decide("file.write", insideArgs).Decision} "
            + $"outside={governance.Decide("file.write", outsideArgs).Decision} ({outsidePath})");

        // Enforced inside a real turn: the model asks for a denied tool, the turn still completes.
        var (store5, _) = JsonlSessionStore.Open(root, clock);
        await using (store5)
        {
            var queue6 = new JsonlInputQueue(root, capacity: 8, clock: clock);
            await queue6.EnqueueAsync(new InputItem
            {
                ItemId = Guid.NewGuid().ToString("n"),
                Source = InputSource.Text,
                Payload = "delete everything",
                IdempotencyKey = "text:client1:msg-4",
            });
            var claim = await queue6.TakeAsync();

            var tools = new InMemoryToolRegistry();
            tools.Register(new StubTool("rm.rf", "deleted the world"));
            var llm = new ScriptedLlmClient(new[]
            {
                new LlmResponse(null, new[] { new LlmToolCall("c1", "rm.rf", "{}") }, "tool_calls"),
                new LlmResponse("i did not do that", Array.Empty<LlmToolCall>(), "stop"),
            });

            var runner = new AgentTurnRunner(
                store5, llm, tools, governance, new DenyAllApprovalHandler(), new HookRegistry());

            var governed = await runner.RunAsync(claim!);

            // Tool call/result are structured events, not rendered content: read the raw log.
            SessionEvent? toolResult = null;
            await foreach (var e in store5.ReadAllAsync())
            {
                if (e.Kind == SessionEventKind.ToolResult)
                {
                    toolResult = e;
                }
            }

            Check("governance: denial is enforced inside a real turn",
                governed.Reason == TurnEndReason.Completed
                && toolResult is not null
                && toolResult.Body.StartsWith("denied:", StringComparison.Ordinal),
                toolResult?.Body ?? "(no tool result)");

            await queue6.CompleteAsync(claim!.ItemId, InputItemStatus.Done);
            await queue6.DisposeAsync();
        }

        // ---- 10. M7: MCP stdio client, bridged into a governed turn -------------------
        var mcpDll = FindMcpTestServer();
        if (mcpDll is null)
        {
            Check("mcp: test-double server is built", false,
                "tests/Partner.McpTestServer/bin/*/net8.0/Partner.McpTestServer.dll not found — run dotnet build first");
        }
        else
        {
            var mcp = new StdioMcpClient(new McpServerConfig
            {
                Name = "smoke-mcp",
                Command = "dotnet",
                Arguments = new[] { mcpDll },
                CallTimeout = TimeSpan.FromSeconds(10),
            });

            try
            {
                await mcp.StartAsync();
                var descriptors = await mcp.ListToolsAsync();
                Check("mcp: server starts and lists tools",
                    mcp.IsRunning && descriptors.Count == 3 && descriptors.Any(d => d.Name == "echo"),
                    string.Join(",", descriptors.Select(d => d.Name)));

                var echo = await mcp.CallToolAsync("echo", """{"text":"round trip"}""");
                Check("mcp: tool call round-trips through the client",
                    echo.Ok && echo.Content == "round trip",
                    $"ok={echo.Ok} content={echo.Content}");

                // The MCP tool goes through the same registry + governance + turn path as any local tool.
                var (store6, _) = JsonlSessionStore.Open(root, clock);
                await using (store6)
                {
                    var queue7 = new JsonlInputQueue(root, capacity: 8, clock: clock);
                    await queue7.EnqueueAsync(new InputItem
                    {
                        ItemId = Guid.NewGuid().ToString("n"),
                        Source = InputSource.Text,
                        Payload = "echo something",
                        IdempotencyKey = "text:client1:msg-5",
                    });
                    var claim = await queue7.TakeAsync();

                    var tools = new InMemoryToolRegistry();
                    foreach (var descriptor in descriptors)
                    {
                        tools.Register(new McpTool(mcp, descriptor));
                    }

                    var llm = new ScriptedLlmClient(new[]
                    {
                        new LlmResponse(null, new[] { new LlmToolCall("c1", "echo", """{"text":"from the model"}""") }, "tool_calls"),
                        new LlmResponse("done", Array.Empty<LlmToolCall>(), "stop"),
                    });

                    var mcpPolicy = new PolicyToolGovernance(ToolPolicy.Parse("""
                        { "tools": { "echo": { "decision": "allow" } } }
                        """));

                    var runner = new AgentTurnRunner(
                        store6, llm, tools, mcpPolicy, new DenyAllApprovalHandler(), new HookRegistry());

                    var mcpTurn = await runner.RunAsync(claim!);

                    SessionEvent? echoResult = null;
                    await foreach (var e in store6.ReadAllAsync())
                    {
                        if (e.Kind == SessionEventKind.ToolResult)
                        {
                            echoResult = e;
                        }
                    }

                    Check("mcp: mcp tool runs inside a governed turn",
                        mcpTurn.Reason == TurnEndReason.Completed
                        && echoResult is not null
                        && echoResult.Body == "from the model"
                        && echoResult.Meta!["ok"] == "true",
                        echoResult?.Body ?? "(no tool result)");

                    await queue7.CompleteAsync(claim!.ItemId, InputItemStatus.Done);
                    await queue7.DisposeAsync();
                }
            }
            finally
            {
                await mcp.StopAsync();
                await mcp.DisposeAsync();
            }
        }

        // ---- 11. M9: desktop layer (read-only checks; the smoke never injects input) --
        var windows = WindowEnumerator.List();
        Check("desktop: window enumeration finds visible windows",
            windows.Count > 0 && windows.All(w => w.Handle != 0 && w.Width > 0 && w.Height > 0),
            $"windows={windows.Count}");

        var captureTarget = windows.FirstOrDefault(w => w.Width >= 100 && w.Height >= 100);
        if (captureTarget is null)
        {
            Check("desktop: GDI capture returns pixels", false, "no window large enough to capture");
        }
        else
        {
            var capture = new GdiScreenCapture();
            var frame = await capture.CaptureWindowAsync(captureTarget.Handle);
            Check("desktop: GDI capture returns pixels",
                frame.Ok && frame.Bgra.Length == frame.Width * frame.Height * 4 && frame.Method.StartsWith("gdi", StringComparison.Ordinal),
                $"ok={frame.Ok} {frame.Width}x{frame.Height} method={frame.Method} err={frame.Error}");

            // Real end-to-end verification against two live captures — no input injected.
            var verifier = new PostActionVerifier(capture);
            var verify = await verifier.VerifyAsync(
                captureTarget.Handle,
                new PixelRect(captureTarget.X, captureTarget.Y, captureTarget.Width, captureTarget.Height),
                frame);

            Check("desktop: post-action verification measures rather than assuming",
                verify.Decision != VerifyDecision.Indeterminate,
                $"decision={verify.Decision} ratio={verify.IntersectedAreaRatio:P2} elapsed={verify.ElapsedMs:0.0}ms "
                + $"budget={verifier.Budget.TotalMilliseconds:0}ms");
        }

        using (var automation = new DesktopAutomation())
        {
            var observation = automation.Observe();
            Check("desktop: lease is issued per entity with a unique generation",
                observation.Entities.Count > 0
                && observation.Entities.All(e => e.Generation == observation.ObsId)
                && automation.Leases.Count == observation.Entities.Count,
                $"entities={observation.Entities.Count} leases={automation.Leases.Count}");

            var refused = await automation.ActAsync(
                "deadbeef", ActionKind.MovePointer, new ActionParams { X = 1, Y = 1 });
            Check("desktop: action on an unknown entity is refused without injecting",
                !refused.GuardPassed && refused.GuardReason == "entity_not_found" && refused.Injection is null,
                refused.GuardReason ?? "(none)");
        }

        var desktopClock = new OffsetClock(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        using (var expiring = new DesktopAutomation(
                   ttlPolicy: new LeaseTtlPolicy { BaseMs = 1_000, FloorMs = 1_000 },
                   clock: desktopClock))
        {
            var observation = expiring.Observe();
            var entity = observation.Entities.First();
            desktopClock.Advance(TimeSpan.FromMinutes(5));

            var refused = await expiring.ActAsync(entity.EntityId, ActionKind.MovePointer, new ActionParams());
            Check("desktop: an expired lease refuses the action without injecting",
                !refused.GuardPassed && refused.GuardReason == "Expired" && refused.Injection is null,
                refused.GuardReason ?? "(none)");
        }

        Check("desktop: input marshalling matches the Win32 contract",
            InputInjector.InputSize == (IntPtr.Size == 8 ? 40 : 28),
            $"INPUT size = {InputInjector.InputSize}");

        // ---- 12. M10/M11: voice, driven by synthetic audio + a fake ASR/TTS server ------
        using (var server = new FakeVoiceServer(transcript: "turn on the desk lamp"))
        {
            var gate = new PlaybackGate();
            var source = new SyntheticAudioSource();
            var asr = new HttpAsrClient(new AsrOptions { BaseUrl = server.BaseUrl, Language = "Chinese" });

            var voiceQueue = new JsonlInputQueue(root, capacity: 8, clock: clock);
            var inputSetting = new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = 0.03,
                UtteranceGapMs = 1200,
                WindowSeconds = 8,
            };

            var log = new List<string>();
            var input = new VoiceInputHalf(
                inputSetting,
                source,
                asr,
                gate,
                utterance => voiceQueue.EnqueueAsync(new InputItem
                {
                    ItemId = utterance.UtteranceId,
                    Source = InputSource.Voice,
                    Payload = utterance.Transcript,
                    IdempotencyKey = utterance.IdempotencyKey,
                }).AsTask(),
                log.Add);

            input.Start();

            // 0.6 s of speech then 1.4 s of silence, fed as 20 ms frames on the real stream clock.
            var frame = 320; // 20 ms at 16 kHz
            for (var i = 0; i < 30; i++)
            {
                source.Emit(0.5f, frame);
                await input.TickAsync();
            }

            for (var i = 0; i < 70; i++)
            {
                source.Emit(0f, frame);
                await input.TickAsync();
            }

            Check("voice: speech followed by silence produces one utterance",
                input.Utterances.Count == 1 && input.Utterances[0].Transcript == "turn on the desk lamp",
                $"utterances={input.Utterances.Count} text={input.Utterances[0].Transcript}");

            Check("voice: the utterance becomes a queue item with a boot-scoped key",
                input.Utterances.Count == 1
                && input.Utterances[0].IdempotencyKey
                    == $"voice:{input.DeviceId}:{input.BootId}:{input.Utterances[0].SegStartMs:0}",
                input.Utterances.Count == 1 ? input.Utterances[0].IdempotencyKey : "(none)");

            Check("voice: the ASR request carried a 16 kHz mono WAV",
                server.LastAsrBody is not null
                && server.LastAsrBody.Contains("\"format\":\"wav\"", StringComparison.Ordinal)
                && server.LastAsrBody.Contains("language Chinese<asr_text>", StringComparison.Ordinal),
                server.LastAsrBody is null ? "(no request)" : $"{server.LastAsrBody.Length} bytes");

            // Output half: markdown must not be read aloud, sentences must play in order.
            var sink = new SyntheticAudioSink();
            var tts = new HttpTtsClient(new TtsOptions { BaseUrl = server.BaseUrl });
            var outputSetting = new VoiceOutputSetting { Enabled = true, MuteDuringTts = true, DrainMs = 0 };
            var output = new VoiceOutputHalf(outputSetting, sink, tts, gate, log.Add);

            var spoken = output.OnText("## Answer\n\nUse **dark** mode. Thanks! 🎉", sessionEventSeq: 42);
            await output.FlushTurnAsync();

            Check("voice: output speaks sentences in order, cleaned of markdown",
                spoken.Count >= 2
                && output.Items.All(i => i.Status is SpeechStatus.Played)
                && output.Items.All(i => !i.Text.Contains("**", StringComparison.Ordinal)
                    && !i.Text.Contains("#", StringComparison.Ordinal))
                && output.Items.Select(i => i.Seq).SequenceEqual(Enumerable.Range(1, output.Items.Count)),
                $"sentences={spoken.Count} statuses={string.Join(",", output.Items.Select(i => i.Status))}");

            Check("voice: playback closed the gate",
                gate.Engaged || output.Items.Count == 0,
                $"engaged={gate.Engaged} items={output.Items.Count}");

            // While the gate is closed, input is discarded — and the drop is reported.
            var utterancesBefore = input.Utterances.Count;
            for (var i = 0; i < 20; i++)
            {
                source.Emit(0.5f, frame);
                await input.TickAsync();
            }

            Check("voice: input during playback is discarded and reported",
                input.Utterances.Count == utterancesBefore && input.DetectedSpeechWhileGated,
                $"utterances={input.Utterances.Count} detectedSpeech={input.DetectedSpeechWhileGated}");

            // Drain the device, let the gate reopen, and confirm the discard is reported with its duration.
            sink.MarkDrained();
            var released = output.TryReleaseGate(input.PendingBlock, input.AsrBusy, 0);
            await input.TickAsync();

            Check("voice: reopening the gate reports the discarded audio",
                released && !gate.Engaged && gate.SkippedSeconds > 0
                && log.Any(l => l.Contains("discarded", StringComparison.Ordinal)),
                $"released={released} skipped={gate.SkippedSeconds:0.00}s blockers={gate.LastBlockers ?? "(none)"}");

            // The gate only reopens once every condition clears, and says why when it does not.
            var stillClosed = output.Conditions(input.PendingBlock, input.AsrBusy);
            output.TryReleaseGate(input.PendingBlock, input.AsrBusy, 0);
            Check("voice: gate reports its blockers while closed",
                stillClosed.AllClear || gate.LastBlockers is not null || gate.LastReleasedAt is not null,
                gate.LastBlockers ?? "(released)");

            input.Stop();
            input.Dispose();
            output.Dispose();
            await voiceQueue.DisposeAsync();
        }

        // Threshold misconfiguration must be detected, not discovered by silence (R11).
        using (var server = new FakeVoiceServer())
        {
            var gate = new PlaybackGate();
            var source = new SyntheticAudioSource();
            var log = new List<string>();
            using var input = new VoiceInputHalf(
                new VoiceInputSetting { Enabled = true, SilenceThreshold = 0.0 },
                source,
                new HttpAsrClient(new AsrOptions { BaseUrl = server.BaseUrl }),
                gate,
                _ => Task.CompletedTask,
                log.Add);

            input.Start();
            for (var i = 0; i < 40; i++)
            {
                source.Emit(0.05f, 320);
                await input.TickAsync();
            }

            Check("voice: a threshold at or below the noise floor is detected",
                input.ThresholdMisconfigured && log.Any(l => l.Contains("silence can never be detected", StringComparison.Ordinal)),
                $"ambient={input.AmbientRms:0.0000} misconfigured={input.ThresholdMisconfigured}");
        }

        // A dead ASR service must be visible and must not fabricate an empty utterance.
        using (var deadAsr = new HttpAsrClient(new AsrOptions
               {
                   BaseUrl = "http://127.0.0.1:9",
                   ConnectTimeout = TimeSpan.FromMilliseconds(300),
                   ReadTimeout = TimeSpan.FromMilliseconds(300),
               }))
        {
            var gate = new PlaybackGate();
            var source = new SyntheticAudioSource();
            var log = new List<string>();
            using var input = new VoiceInputHalf(
                new VoiceInputSetting { Enabled = true, UtteranceGapMs = 400 },
                source,
                deadAsr,
                gate,
                _ => Task.CompletedTask,
                log.Add);

            input.Start();
            for (var i = 0; i < 20; i++)
            {
                source.Emit(0.5f, 320);
                await input.TickAsync();
            }

            for (var i = 0; i < 30; i++)
            {
                source.Emit(0f, 320);
                await input.TickAsync();
            }

            Check("voice: an unreachable ASR service is reported and produces no utterance",
                input.Utterances.Count == 0
                && log.Any(l => l.Contains("transcription failed", StringComparison.Ordinal))
                && !input.AsrBusy,
                $"utterances={input.Utterances.Count} logged={log.Count(l => l.Contains("transcription failed", StringComparison.Ordinal))}");
        }

        // A dead TTS service must not stall playback: the failed sentence still gets a tombstone.
        using (var deadTts = new HttpTtsClient(new TtsOptions
               {
                   BaseUrl = "http://127.0.0.1:9",
                   Timeout = TimeSpan.FromMilliseconds(300),
               }))
        {
            var gate = new PlaybackGate();
            var sink = new SyntheticAudioSink();
            var log = new List<string>();
            using var output = new VoiceOutputHalf(
                new VoiceOutputSetting { Enabled = true, DrainMs = 0 },
                sink,
                deadTts,
                gate,
                log.Add);

            output.OnText("this will fail.", sessionEventSeq: 1);
            var played = await output.PlayPendingAsync();

            Check("voice: a dead TTS service fails visibly without stalling playback",
                played == 0
                && output.Items.All(i => i.Status == SpeechStatus.Failed)
                && output.Items.All(i => i.Error is not null)
                && log.Any(l => l.Contains("synthesis failed", StringComparison.Ordinal)),
                $"items={output.Items.Count} status={output.Items.FirstOrDefault()?.Status}");
        }

        // Voice output disabled: nothing is synthesized at all.
        using (var server = new FakeVoiceServer())
        {
            var requestsBefore = server.Requests.Count;
            var sink = new SyntheticAudioSink();
            using var output = new VoiceOutputHalf(
                new VoiceOutputSetting { Enabled = false },
                sink,
                new HttpTtsClient(new TtsOptions { BaseUrl = server.BaseUrl }),
                new PlaybackGate());

            output.OnText("should not be spoken", 1);
            await output.FlushTurnAsync();

            Check("voice: output disabled means no synthesis and no playback",
                output.Items.Count == 0 && sink.Segments.Count == 0 && server.Requests.Count == requestsBefore,
                $"items={output.Items.Count} segments={sink.Segments.Count}");
        }

        // ---- 13. M12: game layer over a real child process --------------------------
        var gameDll = FindGameTestServer();
        if (gameDll is null)
        {
            Check("game: test server is built", false,
                "tests/Partner.GameTestServer/bin/*/net8.0/partner-game-testserver.dll not found — run dotnet build first");
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

        // ---- 14. M13/M14: UI data layer (the four views) over real HTTP ---------------
        var uiWorkspace = Path.Combine(root, "ui");
        var apiPort = FreePort();
        var uiToken = TokenAuth.Generate();
        var (apiApp, apiContext) = await ApiHost.BuildAsync(uiWorkspace, apiPort, uiToken, clock);
        var apiRun = apiApp.RunAsync();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", uiToken);

        try
        {
            // Wait for the host to accept connections (this also proves the token is accepted).
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    using var probe = await http.GetAsync("/api/health");
                    if (probe.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50);
                }
            }

            // Authentication is fail-closed: the middleware protects every endpoint, health included.
            using var anonymous = new HttpClient { BaseAddress = http.BaseAddress! };
            using var wrongToken = new HttpClient { BaseAddress = http.BaseAddress! };
            wrongToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

            var anonymousHealth = await anonymous.GetAsync("/api/health");
            var anonymousSession = await anonymous.GetAsync("/api/session");
            var wrongTokenSession = await wrongToken.GetAsync("/api/session");
            var authorizedSession = await http.GetAsync("/api/session");

            Check("ui: an unauthenticated request is rejected on every endpoint",
                anonymousHealth.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && anonymousSession.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                $"health={anonymousHealth.StatusCode} session={anonymousSession.StatusCode}");

            Check("ui: a wrong token is rejected and the right one is accepted",
                wrongTokenSession.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && authorizedSession.IsSuccessStatusCode,
                $"wrong={wrongTokenSession.StatusCode} right={authorizedSession.StatusCode}");

            // Seed a session with a user turn and a tool call, then edit the user turn.
            var userSeq = await apiContext.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = "turn on teh lamp",
            });
            await apiContext.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.ToolCall,
                Source = InputSource.Text,
                Body = "lamp.set",
            });

            var editResponse = await http.PostAsJsonAsync(
                $"/api/session/{userSeq}/edit",
                new { body = "turn on the lamp", actor = "human" });
            editResponse.EnsureSuccessStatusCode();

            var sessionView = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var editedSessionItem = sessionView.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("seq").GetInt64() == userSeq);

            var revisions = await http.GetFromJsonAsync<JsonElement>($"/api/session/{userSeq}/revisions");

            // "Editing re-runs nothing" is proved on the raw log: exactly one new event (the edit itself),
            // and the tool-call count is unchanged.
            var uiRawEvents = new List<SessionEvent>();
            await foreach (var e in apiContext.Session.ReadAllAsync())
            {
                uiRawEvents.Add(e);
            }

            var toolCalls = uiRawEvents.Count(e => e.Kind == SessionEventKind.ToolCall);
            var edits = uiRawEvents.Count(e => e.Kind == SessionEventKind.Edit);

            Check("ui: session view shows the edit, keeps the original, and re-runs nothing",
                editedSessionItem.GetProperty("body").GetString() == "turn on the lamp"
                && editedSessionItem.GetProperty("originalBody").GetString() == "turn on teh lamp"
                && editedSessionItem.GetProperty("revisionCount").GetInt32() == 1
                && revisions.GetArrayLength() == 1
                && uiRawEvents.Count == 3
                && toolCalls == 1
                && edits == 1,
                $"body={editedSessionItem.GetProperty("body").GetString()} revisions={revisions.GetArrayLength()} "
                + $"rawEvents={uiRawEvents.Count} toolCalls={toolCalls} edits={edits}");

            // Memory: write, edit (human), then delete.
            await apiContext.Memory.WriteAsync(new MemoryRecord
            {
                RecordId = "ui-mem-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Global,
                Body = "the user prefers dark mode",
                Importance = 0.9,
            });

            var memEdit = await http.PostAsJsonAsync(
                "/api/memory/ui-mem-1/edit",
                new { body = "the user prefers dark mode everywhere", actor = "human" });
            memEdit.EnsureSuccessStatusCode();

            var memRevisions = await http.GetFromJsonAsync<JsonElement>("/api/memory/ui-mem-1/revisions");
            var recall = await http.PostAsJsonAsync("/api/memory/recall", new { query = "dark mode everywhere" });
            var recallBody = await recall.Content.ReadFromJsonAsync<JsonElement>();

            Check("ui: memory edit records the actor and the revision",
                memRevisions.GetArrayLength() == 1
                && memRevisions[0].GetProperty("actor").GetString() == "Human"
                && recallBody.GetArrayLength() >= 1
                && recallBody[0].GetProperty("reasons").GetArrayLength() > 0,
                $"revisions={memRevisions.GetArrayLength()} hits={recallBody.GetArrayLength()}");

            var delete = await http.DeleteAsync("/api/memory/ui-mem-1");
            delete.EnsureSuccessStatusCode();

            var afterDelete = await http.PostAsJsonAsync("/api/memory/recall", new { query = "dark mode everywhere" });
            var afterDeleteBody = await afterDelete.Content.ReadFromJsonAsync<JsonElement>();
            var memList = await http.GetFromJsonAsync<JsonElement>("/api/memory");

            Check("ui: memory delete removes it from search and from the list",
                afterDeleteBody.GetArrayLength() == 0 && memList.GetArrayLength() == 0,
                $"hits={afterDeleteBody.GetArrayLength()} listed={memList.GetArrayLength()}");

            // Projection rebuild must reproduce the same view, entry by entry.
            var beforeRebuild = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var rebuild = await http.PostAsync("/api/projections/rebuild", content: null);
            rebuild.EnsureSuccessStatusCode();
            var afterRebuild = await http.GetFromJsonAsync<JsonElement>("/api/session");

            Check("ui: rebuilding projections reproduces the same session view",
                beforeRebuild.GetProperty("items").ToString() == afterRebuild.GetProperty("items").ToString()
                && beforeRebuild.GetProperty("lastSeq").GetInt64() == afterRebuild.GetProperty("lastSeq").GetInt64(),
                $"before={beforeRebuild.GetProperty("items").GetArrayLength()} after={afterRebuild.GetProperty("items").GetArrayLength()}");

            // Settings round trip.
            var settings = await http.GetFromJsonAsync<JsonElement>("/api/settings");
            var putSettings = await http.PutAsJsonAsync("/api/settings", new
            {
                inputEnabled = true,
                utteranceGapMs = 900,
                outputEnabled = true,
                muteDuringTts = true,
            });
            putSettings.EnsureSuccessStatusCode();
            var saved = await http.GetFromJsonAsync<JsonElement>("/api/settings");

            Check("ui: settings round-trip through the API",
                settings.GetProperty("utteranceGapMs").GetDouble() == 1200
                && saved.GetProperty("utteranceGapMs").GetDouble() == 900
                && saved.GetProperty("inputEnabled").GetBoolean(),
                $"default={settings.GetProperty("utteranceGapMs").GetDouble()} saved={saved.GetProperty("utteranceGapMs").GetDouble()}");

            // Queue view + cancel.
            var queuedSeq = await apiContext.Queue.EnqueueAsync(new InputItem
            {
                ItemId = "ui-queued",
                Source = InputSource.Text,
                Payload = "queued from the ui",
                IdempotencyKey = "text:ui:1",
            });
            var queueView = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var cancel = await http.PostAsync("/api/queue/ui-queued/cancel", content: null);
            cancel.EnsureSuccessStatusCode();
            var queueAfter = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var canceledItem = queueAfter.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("itemId").GetString() == "ui-queued");

            Check("ui: queue view shows state and honours cancel",
                queuedSeq == 1
                && queueView.GetProperty("depth").GetInt32() == 1
                && canceledItem.GetProperty("status").GetString() == "Canceled",
                $"seq={queuedSeq} depth={queueView.GetProperty("depth").GetInt32()} status={canceledItem.GetProperty("status").GetString()}");
        }
        finally
        {
            await apiApp.StopAsync();
            await apiRun;
            await apiContext.DisposeAsync();
        }
    }

    /// <summary>Asks the OS for a free port (the API host must not collide with anything).</summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Locates the built synthetic game server, walking up to the solution root.</summary>
    private static string? FindGameTestServer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var bin = Path.Combine(directory.FullName, "tests", "Partner.GameTestServer", "bin");
        return Directory.Exists(bin)
            ? Directory.GetFiles(bin, "partner-game-testserver.dll", SearchOption.AllDirectories)
                .Where(m => m.Contains("net8.0", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    /// <summary>Minimal reflex behavior for the smoke run.</summary>
    private sealed class Behavior(
        string id,
        Func<ReflexApi, CancellationToken, Task> run,
        Func<ReflexContextState, double> score) : IReflexBehavior
    {
        public string Id { get; } = id;

        public IReadOnlyList<string> Modes { get; } = Array.Empty<string>();

        public double CooldownMs => 0;

        public bool When(ReflexContextState context, ReflexApi api) => true;

        public double Score(ReflexContextState context, ReflexApi api) => score(context);

        public Task RunAsync(ReflexApi api, CancellationToken ct) => run(api, ct);
    }

    /// <summary>Clock the smoke can move forward to exercise lease expiry.</summary>
    internal sealed class OffsetClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>Locates the built MCP test-double server, walking up to the solution root.</summary>
    private static string? FindMcpTestServer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var pattern = Path.Combine("tests", "Partner.McpTestServer", "bin", "*", "net8.0", "Partner.McpTestServer.dll");
        var root = directory.FullName;
        var matches = Directory.Exists(Path.Combine(root, "tests"))
            ? Directory.GetFiles(Path.Combine(root, "tests", "Partner.McpTestServer", "bin"), "Partner.McpTestServer.dll",
                SearchOption.AllDirectories)
            : Array.Empty<string>();

        return matches
            .Where(m => m.Contains("net8.0", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Writes a skill directory into the smoke workspace (null name/description = no front matter).</summary>
    private static void WriteSkill(string workspace, string skillId, string? name, string? description, string body)
    {
        var dir = Path.Combine(workspace, "skills", skillId);
        Directory.CreateDirectory(dir);

        var text = name is null || description is null
            ? body
            : $"---\nname: {name}\ndescription: {description}\n---\n{body}";

        File.WriteAllText(Path.Combine(dir, "SKILL.md"), text);
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Results.Add((what, ok, detail));

        if (ok)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}{(detail is null ? "" : $"  → {detail}")}");
    }

    /// <summary>
    /// Capability declarations: a capability's level is derived from smoke checks that actually ran —
    /// never from configuration (doc §8.5 F / §2.0 rule 5).
    /// </summary>
    private static readonly (string Id, string Owner, string[] Checks)[] Capabilities =
    {
        ("session.append_only_log", "M1", new[]
        {
            "session log opens clean",
            "session has 6 content events (3 user + 3 assistant)",
            "raw log keeps every event (edits included)",
            "restart: session log still clean",
            "restart: last seq preserved",
            "restart: seq continues without reuse",
        }),
        ("session.editable_projection", "M1", new[]
        {
            "edit replaces the projected body",
            "edit keeps the original body",
            "edit is not rendered as its own content",
            "revision count is 1",
            "projection is rebuildable from the log",
        }),
        ("queue.single_fifo", "M2", new[]
        {
            "queue depth counts the three distinct items",
            "running item is visible while executing",
            "consumed in arrival order",
            "queue drained",
            "no running item after drain",
        }),
        ("queue.idempotent_restart", "M2", new[]
        {
            "duplicate idempotency key reuses the seq",
            "restart: consumed idempotency key still dedupes",
            "restart: new item gets a fresh seq",
        }),
        ("queue.crash_recovery", "M2", new[]
        {
            "crash recovery: interrupted item is re-queued",
            "crash recovery: attempt counter advanced",
        }),
        ("agent.turn_state_machine", "M3", new[]
        {
            "turn: item is claimed before the turn",
            "turn: completes with reason=completed",
            "turn: two steps (tool then answer)",
            "turn: event order matches the spec (assistant only when the step has text)",
            "turn: projection shows the answer but not control events",
        }),
        ("agent.tool_loop", "M3", new[]
        {
            "turn: one tool call executed",
        }),
        ("agent.hook_phases", "M3", new[]
        {
            "turn: hook injection is recorded as a system event",
        }),
        ("skills.workspace_scan", "M8", new[]
        {
            "skills: workspace scan finds valid skills and warns about bad ones",
        }),
        ("skills.injection", "M8", new[]
        {
            "skills: injection reaches the model prompt",
            "skills: injection is recorded in the session",
        }),
        ("scheduler.durable_delivery", "M4", new[]
        {
            "scheduler: task delivers one queue item",
            "scheduler: item carries the (taskId, occurrenceAt) key",
            "scheduler: task advances to the next occurrence",
        }),
        ("scheduler.receipt_dedup", "M4", new[]
        {
            "scheduler: restart does not re-deliver the same occurrence",
        }),
        ("scheduler.failure_retention", "M4", new[]
        {
            "scheduler: failed delivery keeps the obligation",
            "scheduler: a failing task is not lost and recovers",
        }),
        ("scheduler.schedule_to_turn", "M4", new[]
        {
            "scheduler: scheduled item is claimable",
            "scheduler: scheduled item runs as a turn",
            "scheduler: session records the schedule turn",
        }),
        ("memory.recall_pipeline", "M5", new[]
        {
            "memory: recall finds the relevant record and not the noise",
            "memory: recall is explainable",
        }),
        ("memory.offline_degradation", "M5", new[]
        {
            "memory: offline (no embeddings) still recalls",
        }),
        ("memory.privacy_delete", "M5", new[]
        {
            "memory: privacy delete purges, hides from search, then compacts",
        }),
        ("memory.promotion", "M5", new[]
        {
            "memory: promotion preserves content as an episodic copy",
        }),
        ("memory.injection_budget", "M5", new[]
        {
            "memory: injection respects the token budget",
        }),
        ("memory.scale_p99", "M5", new[]
        {
            "memory: 100000 records — retrieval P99 under 500ms",
        }),
        ("governance.policy_decisions", "M6", new[]
        {
            "governance: policy allows a listed tool",
            "governance: explicit deny is refused",
        }),
        ("governance.fail_closed", "M6", new[]
        {
            "governance: unlisted tool is refused (fail-closed)",
        }),
        ("governance.target_param_rules", "M6", new[]
        {
            "governance: target parameter rules reject an out-of-scope target",
        }),
        ("governance.enforced_in_turn", "M6", new[]
        {
            "governance: denial is enforced inside a real turn",
        }),
        ("mcp.stdio_lifecycle", "M7", new[]
        {
            "mcp: test-double server is built",
            "mcp: server starts and lists tools",
        }),
        ("mcp.tool_bridge", "M7", new[]
        {
            "mcp: tool call round-trips through the client",
        }),
        ("mcp.governed_turn", "M7", new[]
        {
            "mcp: mcp tool runs inside a governed turn",
        }),
        ("desktop.window_enumeration", "M9", new[]
        {
            "desktop: window enumeration finds visible windows",
        }),
        ("desktop.gdi_capture", "M9", new[]
        {
            "desktop: GDI capture returns pixels",
        }),
        ("desktop.post_action_verification", "M9", new[]
        {
            "desktop: post-action verification measures rather than assuming",
        }),
        ("desktop.lease_gating", "M9", new[]
        {
            "desktop: lease is issued per entity with a unique generation",
            "desktop: action on an unknown entity is refused without injecting",
            "desktop: an expired lease refuses the action without injecting",
        }),
        ("desktop.input_marshalling", "M9", new[]
        {
            "desktop: input marshalling matches the Win32 contract",
        }),
        ("voice.utterance_to_queue", "M10", new[]
        {
            "voice: speech followed by silence produces one utterance",
            "voice: the utterance becomes a queue item with a boot-scoped key",
        }),
        ("voice.asr_contract", "M10", new[]
        {
            "voice: the ASR request carried a 16 kHz mono WAV",
        }),
        ("voice.playback_gate", "M10/M11", new[]
        {
            "voice: playback closed the gate",
            "voice: input during playback is discarded and reported",
            "voice: reopening the gate reports the discarded audio",
            "voice: gate reports its blockers while closed",
        }),
        ("voice.output_ordering", "M11", new[]
        {
            "voice: output speaks sentences in order, cleaned of markdown",
        }),
        ("voice.threshold_guard", "M10", new[]
        {
            "voice: a threshold at or below the noise floor is detected",
        }),
        ("voice.failure_visibility", "M10/M11", new[]
        {
            "voice: an unreachable ASR service is reported and produces no utterance",
            "voice: a dead TTS service fails visibly without stalling playback",
        }),
        ("voice.output_switch", "M11", new[]
        {
            "voice: output disabled means no synthesis and no playback",
        }),
        ("game.process_adapter", "M12", new[]
        {
            "game: adapter attaches to a real game process and reads its state",
            "game: the action outcome carries the post-action state",
            "game: detaching stops the child process",
        }),
        ("game.reflex_loop", "M12", new[]
        {
            "game: a reflex behavior acts and the world changes",
            "game: an async reflex behavior is not preempted mid-run",
        }),
        ("game.sensor_debounce", "M12", new[]
        {
            "game: sensor chatter debounces while action signals never do",
        }),
        ("game.latency_monitor", "M12", new[]
        {
            "game: latency is measured, not assumed",
        }),
        ("ui.auth", "M13", new[]
        {
            "ui: an unauthenticated request is rejected on every endpoint",
            "ui: a wrong token is rejected and the right one is accepted",
        }),
        ("ui.session_view", "M13", new[]
        {
            "ui: session view shows the edit, keeps the original, and re-runs nothing",
        }),
        ("ui.memory_view", "M13", new[]
        {
            "ui: memory edit records the actor and the revision",
            "ui: memory delete removes it from search and from the list",
        }),
        ("ui.queue_view", "M13", new[]
        {
            "ui: queue view shows state and honours cancel",
        }),
        ("ui.settings", "M13", new[]
        {
            "ui: settings round-trip through the API",
        }),
        ("ui.projection_rebuild", "M13", new[]
        {
            "ui: rebuilding projections reproduces the same session view",
        }),
    };

    private static CapabilityManifest BuildManifest()
    {
        // A check name may run more than once (e.g. per loop iteration): every observation must pass.
        var byName = Results
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => g.ToList());

        var entries = Capabilities.Select(c =>
        {
            var missing = c.Checks.Where(n => !byName.ContainsKey(n)).ToArray();
            var observed = c.Checks.Where(byName.ContainsKey).SelectMany(n => byName[n]).ToList();
            var failed = observed.Count(o => !o.Passed);

            var level = observed.Count == 0
                ? CapabilityLevel.NotSupported
                : missing.Length == 0 && failed == 0
                    ? CapabilityLevel.ProductSupported
                    : failed == observed.Count
                        ? CapabilityLevel.NotSupported
                        : CapabilityLevel.Covered;

            return new CapabilityEntry
            {
                Id = c.Id,
                Owner = c.Owner,
                Level = level,
                Evidence = c.Checks.Select(n => new CapabilityCheckEvidence(
                    n,
                    byName.ContainsKey(n),
                    byName.TryGetValue(n, out var runs) && runs.All(r => r.Passed))).ToList(),
            };
        }).ToList();

        return new CapabilityManifest
        {
            ChecksFailed = _failures,
            Capabilities = entries,
        };
    }

    private static void WriteJson<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, CapabilityManifest.Json) + Environment.NewLine);
    }
}
