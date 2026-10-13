using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Partner.Api.Settings;
using Partner.Api.Voice;
using System.Globalization;
using System.Net.Sockets;
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
using Partner.Core.Media;
using Partner.Desktop;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
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
    private static readonly List<(string Name, bool Passed, string? Detail, bool Skipped)> Results = new();
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

        // Not fatal, and not hidden: a capability nobody looked at is named on every run, so the green build
        // cannot be read as "everything was proven" when part of it was opted out.
        foreach (var line in report.NotVerified)
        {
            Console.WriteLine($"  GATE  {line}");
        }

        if (report.Ok)
        {
            var suffix = report.NotVerified.Count > 0
                ? $", {report.NotVerified.Count} not verified this run"
                : string.Empty;
            Console.WriteLine($"  GATE  PASS  no capability regressed ({current.Capabilities.Count} capabilities{suffix})");
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

        WarnIfPartnerAppIsRunning();

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
                Scope = MemoryScope.Retain,
                Body = "the user prefers dark mode in every editor",
                Importance = 0.9,
            });
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "noise-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Retain,
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

            // A hit is one of the records the list shows, so it has to arrive knowing which one: the view
            // filters on these two, and a hit that lost them empties the list under a count that still claims
            // there are hits. The contract test pins the string form over HTTP; this pins the store's own.
            Check("memory: a hit carries the tier and retention of the record it matched",
                hits.Count > 0 && hits[0].Tier == MemoryTier.Semantic && hits[0].Scope == MemoryScope.Retain,
                hits.Count > 0 ? $"tier={hits[0].Tier} scope={hits[0].Scope}" : "(no hits)");

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

            // Injection budget — through the production path now: the recall hook (M0) runs the store's
            // InjectAsync, so the same bounded-then-marked contract applies to a turn's prompt. Here the two
            // arms are checked side by side: the store result, and what the hook writes into a turn.
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
                        Scope = MemoryScope.Retain,
                        Body = "alpha beta gamma delta epsilon zeta eta theta iota kappa",
                    });
                }

                var injection = await tight.InjectAsync("alpha beta");
                Check("memory: injection respects the token budget",
                    injection.Tokens <= 20 && injection.Truncated && injection.Hits.Count < 5,
                    $"tokens={injection.Tokens} hits={injection.Hits.Count} truncated={injection.Truncated}");

                var hook = new MemoryRecallHook(tight);
                var hookCtx = new HookContext
                {
                    SessionId = "smoke",
                    Source = InputSource.Text,
                    InputPayload = "alpha beta",
                    WorkspaceDirectory = root,
                };
                await hook.RunAsync(hookCtx, default);

                var injectedText = string.Join('\n', hookCtx.InjectedPrompts);
                Check("memory: the recall hook injects through the budget and marks the cut",
                    hookCtx.InjectedPrompts.Count == 1
                    && injectedText.Contains("token budget", StringComparison.Ordinal)
                    && injectedText.Contains("alpha beta gamma", StringComparison.Ordinal),
                    $"prompts={hookCtx.InjectedPrompts.Count} truncatedMark={injectedText.Contains("token budget", StringComparison.Ordinal)}");
            }

            // Maintenance pass (M0): a store nobody reads or writes still ages and prunes itself.
            await using (var dusty = new MemoryStore(
                Path.Combine(root, "memory", "dusty.db"),
                clock: clock))
            {
                var longAgo = clock.GetUtcNow() - TimeSpan.FromDays(90) - TimeSpan.FromMinutes(5);
                await dusty.WriteAsync(new MemoryRecord
                {
                    RecordId = "dusty-idle",
                    Tier = MemoryTier.Semantic,
                    Scope = MemoryScope.Recall,
                    Body = "an idle record nobody recalled in over 90 days",
                    CreatedAt = longAgo,
                });
                await dusty.WriteAsync(new MemoryRecord
                {
                    RecordId = "dusty-expired",
                    Tier = MemoryTier.Working,
                    Scope = MemoryScope.Recall,
                    Body = "a working record whose TTL is long blown",
                    CreatedAt = clock.GetUtcNow() - TimeSpan.FromDays(30),
                });
                await dusty.PromoteEligibleAsync();

                var maintenance = new MemoryMaintenanceService(dusty, clock);
                var dustRun = await maintenance.RunAsync();
                await maintenance.DisposeAsync();

                Check("memory: the maintenance pass ages and trims an untouched store",
                    dustRun.Demoted >= 1
                    && dustRun.Trimmed >= 1
                    && (await dusty.GetAsync("dusty-idle"))!.Tier == MemoryTier.Episodic
                    && await dusty.GetAsync("dusty-expired") is null
                    && await dusty.GetAsync(MemoryStore.EpisodicCopyId("dusty-expired")) is not null,
                    $"demoted={dustRun.Demoted} trimmed={dustRun.Trimmed}");
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
                    Scope = MemoryScope.Retain,
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

            // Warm-up calls are discarded on purpose. The first few recalls on a freshly seeded store pay for
            // SQLite's page cache and the FTS tokenizer's own first pass, and they are not what a person
            // waits for: every call after the first few costs a fraction of them. Sampling the cold calls
            // measures start-up, and would fail a store that answers every real query in 80ms.
            for (var i = 0; i < 3; i++)
            {
                await memory.RecallAsync(queries[i % queries.Length]);
            }

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

        // The point a click is delivered to is decided by the OS, not by the rectangle a caller named. This is
        // the read-only half of that rule: the hit test must resolve to a real top-level window (a child
        // handle would make the comparison against an observed window meaningless) and that window must
        // contain the point (a virtualized coordinate would land somewhere else entirely).
        var probeWindow = windows.FirstOrDefault(w => w.Width >= 200 && w.Height >= 200);
        if (probeWindow is null)
        {
            Check("desktop: the window under a point is the top-level window that contains it", false, "no window large enough to probe");
        }
        else
        {
            var probeX = probeWindow.X + probeWindow.Width / 2;
            var probeY = probeWindow.Y + probeWindow.Height / 2;
            var under = HitTest.At(probeX, probeY);

            var contains = false;
            var isRoot = false;
            if (under.Handle != 0)
            {
                var bounds = WindowEnumerator.GetBounds(under.Handle);
                contains = probeX >= bounds.Left && probeX < bounds.Right
                    && probeY >= bounds.Top && probeY < bounds.Bottom;
                isRoot = NativeMethods.GetAncestor(under.Handle, NativeMethods.GaRoot) == under.Handle;
            }

            Check("desktop: the window under a point is the top-level window that contains it",
                under.Handle != 0 && isRoot && contains,
                $"({probeX},{probeY}) -> {under.Handle:x} '{under.Title}' contains={contains} root={isRoot}");
        }

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

            // A named control is acted on through its UI Automation pattern when it has one. What can be
            // checked without touching anything live is the routing decision: an action no pattern can express
            // must come back as "not applicable", which is what tells the caller to inject at the element's
            // centre. A non-null here would mean the action was silently swallowed.
            var sample = observation.Elements.FirstOrDefault();
            var handle = sample is null
                ? 0
                : nint.Parse(sample.WindowId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            var routedToInput = sample is not null
                && new[]
                {
                    ActionKind.MovePointer, ActionKind.RightClick, ActionKind.Scroll,
                    ActionKind.TypeText, ActionKind.KeyPress, ActionKind.Drag,
                }.All(kind => UiaTree.TryAct(handle, sample.WindowId, sample.ElementId, kind) is null);

            Check("desktop: an action a pattern cannot express is routed to injected input",
                routedToInput,
                sample is null
                    ? "no UIA elements in this observation"
                    : $"'{sample.Name}' [{sample.Role}]");

            // An id is an index into one observation, so it stops resolving as soon as the tree changes.
            // Substituting "whatever is at that spot now" is how an action lands on the wrong control.
            var notGuessed = sample is not null
                && UiaTree.TryAct(handle, sample.WindowId, $"{sample.WindowId}:9999", ActionKind.LeftClick) is null
                && UiaTree.TryAct(handle, sample.WindowId, "not-an-element-id", ActionKind.LeftClick) is null;

            Check("desktop: a control id that does not resolve is not guessed at",
                notGuessed,
                sample is null ? "no UIA elements in this observation" : sample.ElementId);

            // What goes on the picture the model reasons about. Estimating a position from an unmarked image is
            // where a real session asked for a play button at (1280,1032) and produced (957,948) while
            // describing the button correctly in words. A mark is not an estimate: it names a control the
            // observation found and carries that control's own click point.
            var sampleWindow = sample is null ? null : observation.Find(sample.WindowId);

            var marks = sampleWindow is null
                ? Array.Empty<ScreenshotMark>()
                : ScreenshotMarks.Marks(
                    observation.Elements
                        .Where(element => string.Equals(element.WindowId, sampleWindow.EntityId, StringComparison.Ordinal))
                        .ToList(),
                    sampleWindow.Rect);

            var marksCarryRealPoints = marks.Count > 0
                && marks.Select((mark, position) => mark.Index == position + 1).All(numbered => numbered)
                && marks.All(mark => observation.Elements.Any(element =>
                    string.Equals(element.ElementId, mark.ElementId, StringComparison.Ordinal)
                    && element.Centre == mark.ClickAt));

            Check("desktop: a picture's marks carry each control's real screen point",
                marksCarryRealPoints,
                marks.Count == 0
                    ? "no marks for the sampled window"
                    : $"{marks.Count} marks, first = '{marks[0].Label}' @ ({marks[0].ClickAt.X},{marks[0].ClickAt.Y})");
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

        // The action's answer now carries the state after it: the same pipeline the screenshot tool runs,
        // attached to a result the model was already about to read. Two read-only evidences — when a picture
        // is promised (the decision matrix, which must exclude a refusal, the aim-only kinds and a caller who
        // said off) and what one render yields (a real PNG under a media id the server's px/py mapping can
        // resolve). Both drive a stub capture, so the user's screen stays out of the check.
        {
            var matrix = DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.LeftClick, "off", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: false, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: true, guardPassed: false)
                && !DesktopActTool.WantsPicture(ActionKind.Focus, "auto", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.MovePointer, "auto", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.ScrollIntoView, "auto", supportsImages: true, guardPassed: true);

            Check("desktop: an act answer promises a picture for the kinds that change what is seen",
                matrix
                && new DesktopActTool(
                    new DesktopAutomation(), DesktopToolPolicy.Any, new FileMediaStore(root),
                    supportsImages: true).Spec.Description.Contains("自动附上", StringComparison.Ordinal)
                && new DesktopActTool(
                    new DesktopAutomation(), DesktopToolPolicy.Any, new FileMediaStore(root),
                    supportsImages: true).Spec.ParametersJsonSchema.Contains("feedback", StringComparison.Ordinal),
                matrix ? "matrix holds and the schema advertises it" : "decision matrix broke");

            var workspace = Path.Combine(root, "desktop-act-state");
            Directory.CreateDirectory(workspace);
            const int frameWidth = 320;
            const int frameHeight = 200;
            var frameRect = new PixelRect(40, 30, frameWidth, frameHeight);
            using (var automation = new DesktopAutomation(capture: new PositionEchoCapture(frameRect)))
            {
                var target = new UiEntity
                {
                    EntityId = "1a2b",
                    Generation = "obs-x",
                    Role = "window",
                    Label = "smoke 窗口",
                    Value = "smoke",
                    Rect = frameRect,
                    Confidence = 1.0,
                    IdentityDigest = "digest-x",
                    LayoutDigest = DesktopObservation.Layout(frameRect),
                    ProcessId = 4242,
                    ObservedAtMs = desktopClock.GetUtcNow(),
                };

                var picture = await DesktopScreenshotTool.RenderAfterActionAsync(
                    automation, new FileMediaStore(workspace), target,
                    new List<UiElement> { new("e-1", "1a2b", "播放", "Button", new PixelRect(50, 40, 80, 30), "element-a") },
                    obsId: "obs-x",
                    ct: default);

                var mapped = automation.PictureOf(picture?.MediaId ?? "");
                Check("desktop: an act answer's attached picture maps back and is recorded by id",
                    picture is not null
                    && mapped is not null
                    && frameRect == mapped.ScreenRect
                    && frameWidth == mapped.PictureWidth
                    && frameHeight == mapped.PictureHeight
                    && "obs-x" == mapped.ObsId
                    && picture.DataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                    && automation.LastShownEntityId == target.EntityId,
                    picture is null
                        ? "render produced nothing"
                        : $"{picture.Width}x{picture.Height} media={picture.MediaId} marks={picture.Marks.Count}");
            }
        }

        // The step budget is measured by tool family: a GUI task runs 20–40 look-act-look steps, so a flat
        // single number spent them on the first two controls and ended the turn as step-limit. Keyed on what
        // the registry holds, so the same turn runner gives a plain chat turn only the base number.
        {
            var desktopFamily = new InMemoryToolRegistry();
            desktopFamily.Register(new StubTool("desktop_act", "done"));
            desktopFamily.Register(new StubTool("desktop_observe", "seen"));

            var plainFamily = new InMemoryToolRegistry();
            plainFamily.Register(new StubTool("clock", "12:34"));

            var options = new AgentTurnOptions();
            var desktopBudget = options.StepsAllowedFor(desktopFamily.Specs.Select(spec => spec.Name));
            var plainBudget = options.StepsAllowedFor(plainFamily.Specs.Select(spec => spec.Name));

            Check("desktop: the step budget is measured by tool family, not one number",
                options.MaxSteps == 8
                && desktopBudget > 8
                && plainBudget == 8,
                $"default={options.MaxSteps} desktop={desktopBudget} plain={plainBudget}");
        }

        // Waiting is one call with a deadline instead of the observe-observe loop. The wait for a control
        // that is already there answers at once with a fresh id (a real aimed walk, read-only).
        using (var waitAutomation = new DesktopAutomation())
        {
            var observation = waitAutomation.Observe();
            var target = observation.Entities.FirstOrDefault(entity => !entity.IsMinimized);
            var element = observation.Elements.FirstOrDefault(candidate => candidate.WindowId == target!.EntityId);
            if (target is not null && element is not null)
            {
                var waitTool = new DesktopWaitTool(waitAutomation);
                var found = await waitTool.InvokeAsync($$"""
                    {"kind":"element","entityId":"{{target.EntityId}}","elementId":"{{element.ElementId}}"}
                    """);

                Check("desktop: a wait for a control that is already there answers at once with the new id",
                    found.Ok && found.Content.Contains("控件已出现", StringComparison.Ordinal)
                        && found.Content.Contains("desktop_act 可直接用", StringComparison.Ordinal),
                    found.Content[..Math.Min(70, found.Content.Length)]);
            }
        }

        // —— The desktop level: the observation level under “window” ——
        // The taskbar/tray/notification don't belong to any top-level window, so they never appeared in observations;
        // the full-frame screenshot brings them in along with the window marks, no prerequisite observation.
        using (var scopeAutomation = new DesktopAutomation())
        {
            var workspace = Path.Combine(root, "desktop-scope");
            Directory.CreateDirectory(workspace);
            var scopeTool = new DesktopScreenshotTool(scopeAutomation, new FileMediaStore(workspace));

            var shot = await scopeTool.InvokeAsync("""{"scope":"desktop","maxDimension":4096}""");
            var mediaId = System.Text.RegularExpressions.Regex.Match(
                shot.Content, "媒体 ([0-9a-f]{8,})").Groups[1].Value;
            var mapped = string.IsNullOrEmpty(mediaId) ? null : scopeAutomation.PictureOf(mediaId);
            var expectations = VirtualDesktop.Current();

            Check("desktop: a full-desktop picture exists without any observation and is recorded as desktop",
                shot.Ok
                && shot.Images is { Count: > 0 }
                && mapped is not null
                && mapped.EntityId == "desktop"
                && mapped.ScreenRect == new PixelRect(expectations.X, expectations.Y, expectations.Width, expectations.Height),
                shot.Ok ? $"media={mediaId} marks=?" : shot.Content[..Math.Min(70, shot.Content.Length)]);

            // px/py on a full-frame image -> bare screen coordinate path (forwarding in input-free refusal). Pick the non-foreground
            // window center; if ambiguity/wait-free window is not found, skip (such as full screen / foreground lockup environment).
            var fresh = scopeAutomation.Observe();
            var background = fresh.Entities.FirstOrDefault(entity =>
                !entity.IsForeground && !entity.IsMinimized && entity.Rect.Width > 100);
            if (background is not null && mapped is not null)
            {
                var centreX = background.Rect.X + background.Rect.Width / 2;
                var centreY = background.Rect.Y + background.Rect.Height / 2;
                var px = (int)Math.Round((centreX - mapped.ScreenRect.X) * mapped.PictureWidth / (double)mapped.ScreenRect.Width);
                var py = (int)Math.Round((centreY - mapped.ScreenRect.Y) * mapped.PictureHeight / (double)mapped.ScreenRect.Height);

                var actTool = new DesktopActTool(
                    scopeAutomation, DesktopToolPolicy.Any, new FileMediaStore(workspace));
                var resolved = await actTool.InvokeAsync(
                    $$"""
                    {"kind":"KeyPress","virtualKey":67,"px":{{px}},"py":{{py}},"mediaId":"{{mediaId}}","feedback":"off"}
                    """);

                Check("desktop: px/py on the desktop picture resolves to the point it was taken from",
                    !resolved.Ok
                    && resolved.Content.Contains($"({centreX},{centreY})", StringComparison.Ordinal)
                    && (resolved.Content.Contains("不在前台", StringComparison.Ordinal) ||
                        resolved.Content.Contains("不落在唯一一个已观察窗口内", StringComparison.Ordinal)),
                    resolved.Content[..Math.Min(80, resolved.Content.Length)]);
            }
        }

        // —— px/py 就近元素兜底 ——（安全的证明路径：背景窗口的 key 在 guard 拒绝，不注入；拒绝带出解析后的控件名 = 充分证据）
        using (var snapAutomation = new DesktopAutomation())
        {
            var workspace = Path.Combine(root, "desktop-snap");
            Directory.CreateDirectory(workspace);
            var observation = snapAutomation.Observe();
            var target = observation.Entities.FirstOrDefault(entity =>
                !entity.IsForeground && !entity.IsMinimized && entity.Rect.Width > 100);
            var element = observation.Elements.FirstOrDefault(candidate =>
                candidate.WindowId == target!.EntityId
                && !candidate.IsOffscreen
                && candidate.Role == "Button"
                && !string.IsNullOrEmpty(candidate.Name)
                && candidate.Rect.Width > 10);
            if (target is not null && element is not null)
            {
                var shotTool = new DesktopScreenshotTool(snapAutomation, new FileMediaStore(workspace));
                var shot = await shotTool.InvokeAsync(
                    $$"""{"entityId":"{{target.EntityId}}","maxDimension":4096}""");
                var mediaId = System.Text.RegularExpressions.Regex.Match(
                    shot.Content, "媒体 ([0-9a-f]{8,})").Groups[1].Value;
                var picture = snapAutomation.PictureOf(mediaId);

                // 刻意偏 5px：扣入单击应有的控件邻域（snap 半径 8px）——从图出来的一步误差由服务器摊平。
                var px = (int)Math.Round((element!.Centre.X + 5 - picture!.ScreenRect.X) * picture.PictureWidth / (double)picture.ScreenRect.Width);
                var py = (int)Math.Round((element.Centre.Y - picture.ScreenRect.Y) * picture.PictureHeight / (double)picture.ScreenRect.Height);

                var snapAct = new DesktopActTool(
                    snapAutomation, DesktopToolPolicy.Any, new FileMediaStore(workspace));
                var resolved = await snapAct.InvokeAsync(
                    $$"""{"kind":"KeyPress","virtualKey":67,"px":{{px}},"py":{{py}},"mediaId":"{{mediaId}}","feedback":"off"}""");

                Check("desktop: a px/py miss within the snap radius resolves to the control it was for",
                    !resolved.Ok
                    && resolved.Content.Contains($"'{element.Name}' [{element.Role}]", StringComparison.Ordinal),
                    resolved.Content[..Math.Min(90, resolved.Content.Length)]);
            }
        }

        Check("desktop: input marshalling matches the Win32 contract",
            InputInjector.InputSize == (IntPtr.Size == 8 ? 40 : 28),
            $"INPUT size = {InputInjector.InputSize}");

        // The bug this replaces: every positional mouse event went out without MOUSEEVENTF_ABSOLUTE, so a
        // screen coordinate was applied as a relative mouse delta and each click landed in the corner of the
        // screen. Asserted on the records themselves, because the smoke must not move the user's pointer.
        var pointerProbe = new InputInjector(() => new VirtualDesktop(0, 0, 2560, 1440));
        var positioned = pointerProbe.TryBuildPointer(1280, 720, 0, out var pointerRecords, out _);
        var positionalOk = positioned
            && (pointerRecords[0].Union.Mouse.Flags & NativeMethods.MouseeventfAbsolute) != 0
            && (pointerRecords[0].Union.Mouse.Flags & NativeMethods.MouseeventfVirtualdesk) != 0
            && pointerRecords[0].Union.Mouse.Dx is > 32000 and < 33600
            && pointerRecords[0].Union.Mouse.Dy is > 32000 and < 33600;

        Check("desktop: positional input is absolute in the 0..65535 virtual-desktop space",
            positionalOk,
            positioned
                ? $"dx={pointerRecords[0].Union.Mouse.Dx} dy={pointerRecords[0].Union.Mouse.Dy} "
                    + $"flags=0x{pointerRecords[0].Union.Mouse.Flags:x}"
                : "no records were built");

        // Clamping silently would turn "click the Save button" into "click the screen edge".
        var offScreenRefused = !pointerProbe.TryBuildPointer(99999, 720, 0, out _, out var offScreenError)
            && offScreenError?.Contains("虚拟桌面", StringComparison.Ordinal) == true;

        Check("desktop: a point outside the virtual desktop is refused instead of clamped",
            offScreenRefused,
            offScreenError ?? "(no error reported)");

        // The minimized-window checks run last, on purpose: they take the reader's focus, and
        // anything they invalidated would then look like a regression in code it never ran.

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
        var (apiApp, apiContext, apiConsumer, apiVoice, _) = await ApiHost.BuildAsync(uiWorkspace, apiPort, uiToken, clock);
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
                Scope = MemoryScope.Retain,
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
                input = new { enabled = true, utteranceGapMs = 900 },
                output = new { enabled = true, muteDuringTts = true },
                asr = new { baseUrl = "http://127.0.0.1:45101", model = "smoke-asr", language = "Chinese" },
                tts = new { baseUrl = "http://127.0.0.1:45102", lang = "zh", chunkMs = 120 },
            });
            putSettings.EnsureSuccessStatusCode();
            var saved = await http.GetFromJsonAsync<JsonElement>("/api/settings");

            Check("ui: settings round-trip every section through the API",
                settings.GetProperty("input").GetProperty("utteranceGapMs").GetDouble() == 1200
                && saved.GetProperty("input").GetProperty("utteranceGapMs").GetDouble() == 900
                && saved.GetProperty("input").GetProperty("enabled").GetBoolean()
                && saved.GetProperty("output").GetProperty("muteDuringTts").GetBoolean()
                && saved.GetProperty("asr").GetProperty("model").GetString() == "smoke-asr"
                && saved.GetProperty("tts").GetProperty("chunkMs").GetInt32() == 120,

                // The memory section rides the same PUT: off is the shipped default, so this also asserts the
                // section arrives rather than being dropped by the view.
                $"gap={saved.GetProperty("input").GetProperty("utteranceGapMs").GetDouble()} "
                + $"asr={saved.GetProperty("asr").GetProperty("model").GetString()} "
                + $"tts={saved.GetProperty("tts").GetProperty("chunkMs").GetInt32()}");
            Check("memory: embeddings default off and the switch round-trips",
                settings.GetProperty("memory").GetProperty("embeddings").GetBoolean() == false
                && saved.GetProperty("memory").GetProperty("embeddings").GetBoolean() == false,
                $"default={settings.GetProperty("memory").GetProperty("embeddings").GetBoolean()}"
                + $" afterSave={saved.GetProperty("memory").GetProperty("embeddings").GetBoolean()}");

            // Queue view + cancel. The view is served from what is still in play rather than from the log, so a
            // cancelled item leaves the list instead of appearing in it as Canceled. The check asserts that
            // shape: Queued while waiting, absent once settled, with the depth following it down.
            var queuedSeq = await apiContext.Queue.EnqueueAsync(new InputItem
            {
                ItemId = "ui-queued",
                Source = InputSource.Text,
                Payload = "queued from the ui",
                IdempotencyKey = "text:ui:1",
            });
            var queueView = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var listedBefore = queueView.GetProperty("items").EnumerateArray()
                .FirstOrDefault(i => i.GetProperty("itemId").GetString() == "ui-queued");
            var beforeStatus = listedBefore.ValueKind == JsonValueKind.Object
                ? listedBefore.GetProperty("status").GetString()
                : "(not listed)";
            var cancel = await http.PostAsync("/api/queue/ui-queued/cancel", content: null);
            cancel.EnsureSuccessStatusCode();
            var queueAfter = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var stillListed = queueAfter.GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("itemId").GetString() == "ui-queued");

            Check("ui: queue view shows state and honours cancel",
                queuedSeq == 1
                && queueView.GetProperty("depth").GetInt32() == 1
                && beforeStatus == "Queued"
                && !stillListed
                && queueAfter.GetProperty("depth").GetInt32() == 0,
                $"seq={queuedSeq} depth={queueView.GetProperty("depth").GetInt32()} before={beforeStatus} "
                + $"stillListed={stillListed} depthAfter={queueAfter.GetProperty("depth").GetInt32()}");

            // The composer's entry point: it delivers to the queue and nothing executes it here.
            var sessionBefore = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var submitted = await http.PostAsJsonAsync("/api/input", new { source = "text", payload = "from the composer" });
            var submittedBody = await submitted.Content.ReadFromJsonAsync<JsonElement>();
            var queueAfterInput = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var sessionAfter = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var composerItem = queueAfterInput.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("itemId").GetString() == submittedBody.GetProperty("itemId").GetString());

            Check("ui: submitting from the composer queues work without executing it",
                submitted.IsSuccessStatusCode
                && submittedBody.GetProperty("status").GetString() == "Queued"
                && composerItem.GetProperty("source").GetString() == "Text"
                && sessionAfter.GetProperty("lastSeq").GetInt64() == sessionBefore.GetProperty("lastSeq").GetInt64(),
                $"status={submittedBody.GetProperty("status").GetString()} source={composerItem.GetProperty("source").GetString()} "
                + $"lastSeq {sessionBefore.GetProperty("lastSeq").GetInt64()}->{sessionAfter.GetProperty("lastSeq").GetInt64()}");
        }
        finally
        {
            await apiApp.StopAsync();
            await apiRun;
            await apiVoice.DisposeAsync();
            await apiConsumer.DisposeAsync();
            await apiContext.DisposeAsync();
        }

        // ---- 15. consumer: the queue drains only when a model is configured ----------------
        {
            // Without a model the consumer must not run at all, and it must say so: work stays queued and the
            // state is visible rather than silently stuck.
            var disabledWorkspace = Path.Combine(root, "consumer-off");
            var disabledPort = FreePort();
            var disabledToken = TokenAuth.Generate();
            var (offApp, offContext, offConsumer, offVoice, _) = await ApiHost.BuildAsync(disabledWorkspace, disabledPort, disabledToken, clock);
            var offRun = offApp.RunAsync();
            using var offHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{disabledPort}") };
            offHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", disabledToken);

            try
            {
                await WaitForHostAsync(offHttp);
                var offState = await offHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                await offHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "orphan work" });
                var offQueue = await offHttp.GetFromJsonAsync<JsonElement>("/api/queue");

                Check("consumer: without a model it is disabled, says why, and leaves work queued",
                    !offState.GetProperty("running").GetBoolean()
                    && offState.GetProperty("reason").GetString()!.Contains("PARTNER_LLM", StringComparison.Ordinal)
                    && offQueue.GetProperty("depth").GetInt32() == 1
                    && offState.GetProperty("stats").GetProperty("handled").GetInt32() == 0,
                    $"running={offState.GetProperty("running").GetBoolean()} depth={offQueue.GetProperty("depth").GetInt32()}");

                // Shrinking the window with no model still saves: the endpoint reports that compaction is
                // deferred, rather than a 500 (or a silent loss) — the save itself must never break.
                var offBefore = await offHttp.GetFromJsonAsync<JsonElement>("/api/settings");
                if (!offBefore.TryGetProperty("model", out _))
                {
                    throw new InvalidOperationException("settings view lost the model section");
                }

                var offShrink = await offHttp.PutAsJsonAsync("/api/settings", new
                {
                    model = new
                    {
                        // Record-replace semantics: both limits, and maxTokens must stay below the window.
                        contextTokens = 8192,
                        maxTokens = 2048,
                    },
                });

                var offCompaction = (await offShrink.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("compaction");
                Check("settings: shrinking the window without a model saves and reports the deferred compaction",
                    offShrink.IsSuccessStatusCode
                    && offCompaction.ValueKind == JsonValueKind.Object
                    && !offCompaction.GetProperty("compacted").GetBoolean()
                    && (offCompaction.GetProperty("reason").GetString() ?? string.Empty).Contains("压缩", StringComparison.Ordinal),
                    $"status={(int)offShrink.StatusCode} reason={offCompaction.GetProperty("reason").GetString()}");
            }
            finally
            {
                await offApp.StopAsync();
                await offRun;
                await offVoice.DisposeAsync();
                await offConsumer.DisposeAsync();
                await offContext.DisposeAsync();
            }

            // With a model, the same delivery becomes a turn that lands in the session.
            var onWorkspace = Path.Combine(root, "consumer-on");
            var onPort = FreePort();
            var onToken = TokenAuth.Generate();
            var onLlm = new ScriptedLlmClient(new[]
            {
                new LlmResponse("书房的灯已经打开了。", Array.Empty<LlmToolCall>(), "stop"),
            });
            var (onApp, onContext, onConsumer, onVoice, _) = await ApiHost.BuildAsync(onWorkspace, onPort, onToken, clock, onLlm);
            var onRun = onApp.RunAsync();
            using var onHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{onPort}") };
            onHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", onToken);

            try
            {
                await WaitForHostAsync(onHttp);
                var onState = await onHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                var delivered = await onHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "打开书房的灯" });
                var deliveredId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var settled = await WaitForQueueItemToSettleAsync(onHttp, deliveredId);

                var session = await onHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var entries = session.GetProperty("items").EnumerateArray()
                    .Select(item => (Kind: item.GetProperty("kind").GetString(), Body: item.GetProperty("body").GetString()))
                    .ToList();

                Check("consumer: with a model the delivered input becomes a turn in the session",
                    onState.GetProperty("running").GetBoolean()
                    && settled
                    && entries.Any(e => e.Kind == "User" && e.Body == "打开书房的灯")
                    && entries.Any(e => e.Kind == "Assistant" && e.Body == "书房的灯已经打开了。"),
                    $"running={onState.GetProperty("running").GetBoolean()} settled={settled} entries={entries.Count}");

                var afterState = await onHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                var queueAfter = await onHttp.GetFromJsonAsync<JsonElement>("/api/queue");
                Check("consumer: the queue drains and the loop counts what it handled",
                    queueAfter.GetProperty("depth").GetInt32() == 0
                    && afterState.GetProperty("stats").GetProperty("completed").GetInt32() == 1,
                    $"depth={queueAfter.GetProperty("depth").GetInt32()} completed={afterState.GetProperty("stats").GetProperty("completed").GetInt32()}");
            }
            finally
            {
                await onApp.StopAsync();
                await onRun;
                await onVoice.DisposeAsync();
                await onConsumer.DisposeAsync();
                await onContext.DisposeAsync();
            }

            // The production client over a real socket: a minimal server that speaks the OpenAI shape stands in
            // for a provider, so the whole chain (deliver → loop → HTTP → parse → session) is exercised for real.
            /// <summary>The host holds these files open, so a reader has to allow its writer back.</summary>
            static async Task<string> ReadSharedAsync(string path)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }

            var wireWorkspace = Path.Combine(root, "consumer-wire");
            var wirePort = FreePort();
            var wireToken = TokenAuth.Generate();
            var modelPort = FreePort();

            using var provider = new FakeChatProvider(modelPort, "通过真实 HTTP 返回。");
            var httpClient = new HttpLlmClient(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                new HttpLlmOptions { BaseUrl = $"http://127.0.0.1:{modelPort}/v1", Model = "smoke-model" });

            var (wireApp, wireContext, wireConsumer, wireVoice, _) = await ApiHost.BuildAsync(wireWorkspace, wirePort, wireToken, clock, httpClient);
            var wireRun = wireApp.RunAsync();
            using var wireHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{wirePort}") };
            wireHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wireToken);

            try
            {
                await WaitForHostAsync(wireHttp);
                var delivered = await wireHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "走一次真实 HTTP" });
                var deliveredId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var settled = await WaitForQueueItemToSettleAsync(wireHttp, deliveredId);

                var session = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var assistant = session.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() == "Assistant")
                    .Select(item => item.GetProperty("body").GetString())
                    .ToList();

                var wireQueue = await wireHttp.GetFromJsonAsync<JsonElement>("/api/queue");
                var stillQueued = wireQueue.GetProperty("items").EnumerateArray()
                    .Any(item => item.GetProperty("itemId").GetString() == deliveredId);

                Check("consumer: a real HTTP model call completes a turn end to end",
                    settled
                    && !stillQueued
                    && assistant.Count == 1
                    && assistant[0] == "通过真实 HTTP 返回。"
                    && provider.RequestPath?.Contains("/v1/chat/completions", StringComparison.Ordinal) == true
                    // No key configured means no Authorization header, rather than an empty one.
                    && provider.SawAuthorization is null,
                    $"settled={settled} depth={wireQueue.GetProperty("depth").GetInt32()} "
                    + $"requests=[{string.Join(" | ", provider.Requests)}]");

                // Streaming: the reply has to be readable while it is still being written. The provider releases
                // it two characters at a time with a pause between them, so a poll landing mid-turn sees a
                // partial answer that is longer than the one before it.
                //
                // A second attempt is allowed because a loaded machine can stall past the whole window; the
                // check is about the mechanism, and a broken one never passes either attempt. The deterministic
                // version of this lives in the API tests, which gate the model instead of racing it.
                var sawLive = false;
                var grew = false;
                var firstLive = string.Empty;

                for (var round = 0; round < 2 && !(sawLive && grew); round++)
                {
                    await wireHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "流式一轮" });

                    for (var attempt = 0; attempt < 600 && !(sawLive && grew); attempt++)
                    {
                        var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        var live = polled.GetProperty("items").EnumerateArray()
                            .FirstOrDefault(item => item.GetProperty("streaming").GetBoolean());

                        if (live.ValueKind == JsonValueKind.Object)
                        {
                            var body = live.GetProperty("body").GetString() ?? string.Empty;

                            if (!sawLive)
                            {
                                sawLive = true;
                                firstLive = body;
                            }
                            else if (body.Length > firstLive.Length)
                            {
                                grew = true;
                            }
                        }

                        await Task.Delay(10);
                    }

                    // Let the turn finish before deciding whether to try again.
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        if (!polled.GetProperty("busy").GetBoolean())
                        {
                            break;
                        }

                        await Task.Delay(25);
                    }
                }

                Check("session: the reply is visible while it is still being written",
                    sawLive && grew,
                    $"live={sawLive} grew={grew} first={firstLive}");

                // And the request that produced it really did ask for a stream, rather than getting a whole
                // answer that happened to arrive quickly.
                Check("session: the client asks the model to stream",
                    provider.LastRequestBody?.Contains("\"stream\":true", StringComparison.Ordinal) == true,
                    provider.LastRequestBody is null ? "(no body)" : provider.LastRequestBody[..Math.Min(120, provider.LastRequestBody.Length)]);

                // And when the turn ends, the live row is replaced by the event it stood in for.
                var liveGone = false;
                var finalAssistant = string.Empty;

                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    var streaming = polled.GetProperty("items").EnumerateArray()
                        .Any(item => item.GetProperty("streaming").GetBoolean());
                    var idle = !polled.GetProperty("busy").GetBoolean();

                    if (!streaming && idle)
                    {
                        liveGone = true;
                        finalAssistant = polled.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "Assistant")
                            .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                            .LastOrDefault() ?? string.Empty;
                        break;
                    }

                    await Task.Delay(25);
                }

                Check("session: the finished reply replaces the live row, whole",
                    liveGone && finalAssistant == "通过真实 HTTP 返回。",
                    $"gone={liveGone} body={finalAssistant}");

                // Tool rows: a turn that called a tool must not look like it did nothing. They reach the view
                // (the UI folds them), and the prompt builder ignores them — checked in Core, where the prompt
                // is built.
                await wireContext.Session.AppendAsync(new SessionEvent
                {
                    Kind = SessionEventKind.ToolCall,
                    Source = InputSource.Text,
                    Body = "clock",
                });

                await wireContext.Session.AppendAsync(new SessionEvent
                {
                    Kind = SessionEventKind.ToolResult,
                    Source = InputSource.Text,
                    Body = "{\"now\":\"12:00\"}",
                });

                var withTools = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var kinds = withTools.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("kind").GetString())
                    .ToList();

                Check("session: tool calls and results reach the view",
                    kinds.Contains("ToolCall") && kinds.Contains("ToolResult"),
                    string.Join(",", kinds.Distinct()));
                // Image input: the bytes reach the model inline, and the log keeps only the id. A 1×1 PNG is
                // enough — what is being checked is the shape of the request, not the pixels.
                using (var upload = new ByteArrayContent(Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")))
                {
                    upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    var uploaded = await wireHttp.PostAsync("/api/media", upload);
                    var mediaId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>())
                        .GetProperty("mediaId").GetString();

                    Check("image: an upload comes back as an id",
                        uploaded.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(mediaId),
                        $"status={uploaded.StatusCode} id={mediaId}");

                    await wireHttp.PostAsJsonAsync("/api/input", new
                    {
                        source = "text",
                        payload = "看看这张图",
                        media = new[] { mediaId },
                    });

                    var settledImage = false;
                    for (var attempt = 0; attempt < 400 && !settledImage; attempt++)
                    {
                        var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        settledImage = !polled.GetProperty("busy").GetBoolean()
                            && polled.GetProperty("items").EnumerateArray().Any(item =>
                                item.GetProperty("kind").GetString() == "Assistant"
                                && item.GetProperty("body").GetString() == "通过真实 HTTP 返回。");
                        await Task.Delay(25);
                    }

                    var request = provider.LastRequestBody ?? string.Empty;
                    Check("image: the model receives the attachment inline",
                        settledImage
                        && request.Contains("\"image_url\"", StringComparison.Ordinal)
                        && request.Contains("data:image/png;base64,", StringComparison.Ordinal),
                        $"settled={settledImage} imageUrl={request.Contains("\"image_url\"", StringComparison.Ordinal)}");

                    // The session and queue logs stay text: they hold the id, never the bytes.
                    var sessionLog = await ReadSharedAsync(Path.Combine(wireWorkspace, "session", "events.jsonl"));
                    var queueLog = await ReadSharedAsync(Path.Combine(wireWorkspace, "queue", "items.jsonl"));

                    Check("image: the logs keep the id, not the bytes",
                        queueLog.Contains(mediaId!, StringComparison.Ordinal)
                        && !queueLog.Contains("iVBORw0KGgo", StringComparison.Ordinal)
                        && !sessionLog.Contains("iVBORw0KGgo", StringComparison.Ordinal),
                        $"queueHasId={queueLog.Contains(mediaId!, StringComparison.Ordinal)}");
                }

            // The desktop capability end to end, with a scripted model: model → governance → tool → desktop.
            // Nothing is injected here — the action is denied by the approval queue, which is the path that
            // protects the user's pointer, and the smoke never takes it upon itself to click.
            // The chain the log showed failing: observe → act → approval → lease. The model here is dynamic — it
            // reads what it just observed and asks to press a key at a real window that is *not* in front, so
            // the guards refuse it before anything is injected. What is being proven is what happens *before*
            // that: a slow approval outlives the lease, and the action must still be about the same window.
            var leaseWorkspace = Path.Combine(root, "desktop-lease");
            Directory.CreateDirectory(Path.Combine(leaseWorkspace, "settings"));
            await File.WriteAllTextAsync(
                Path.Combine(leaseWorkspace, "settings", "runtime.json"),
                """{"model":{"supportsImages":true},"desktop":{"enabled":true,"leaseTtlMs":1500,"allowedWindowTitles":[]}}""");

            var leasePort = FreePort();
            var leaseToken = TokenAuth.Generate();
            var leaseLlm = new ObserveThenActLlm();

            var (leaseApp, leaseContext, leaseConsumer, leaseVoice, _) =
                await ApiHost.BuildAsync(leaseWorkspace, leasePort, leaseToken, clock, leaseLlm);
            var leaseRun = leaseApp.RunAsync();
            using var leaseHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{leasePort}") };
            leaseHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", leaseToken);

            try
            {
                await WaitForHostAsync(leaseHttp);
                await leaseHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "按一下 C" });

                // The action parks in the approval queue.
                string? parked = null;
                for (var attempt = 0; attempt < 600 && parked is null; attempt++)
                {
                    var parkedQueue = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/approvals");
                    var pending = parkedQueue.GetProperty("pending").EnumerateArray().ToList();
                    if (pending.Count > 0)
                    {
                        parked = pending[0].GetProperty("approvalId").GetString();
                    }

                    await Task.Delay(25);
                }

                Check("desktop: a key press waits for approval", parked is not null, $"id={parked}");

                var obsBefore = (await leaseHttp.GetFromJsonAsync<JsonElement>("/api/desktop"))
                    .GetProperty("lastObsId").GetString();

                // Wait well past the lease TTL (the policy floors it at 2s), then approve: the lease is long
                // dead, the window is not. Acting without looking again would be refused as Expired here.
                await Task.Delay(6000);
                if (parked is not null)
                {
                    await leaseHttp.PostAsJsonAsync($"/api/approvals/{parked}", new { approved = true });
                }

                string? outcome = null;
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    var results = polled.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                        .ToList();

                    // The keystroke's refusal specifically: by the time the approval lands the turn may
                    // already have moved on, and "the last result" then means something else.
                    var refusal = results
                        .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                        .LastOrDefault(body => body.Contains("前台", StringComparison.Ordinal));

                    if (refusal is not null)
                    {
                        outcome = refusal;
                        break;
                    }

                    await Task.Delay(25);
                }

                // The keystroke was refused (that window is not in front), so nothing was typed — and the tool
                // looked again before deciding, which is what makes a lease that died during the approval
                // harmless. Without the re-look the lease is dead and the action is refused as Expired.
                var obsAfter = (await leaseHttp.GetFromJsonAsync<JsonElement>("/api/desktop"))
                    .GetProperty("lastObsId").GetString();

                Check("desktop: an approved action looks again instead of trusting a dead lease",
                    outcome is not null
                    && outcome.Contains("前台", StringComparison.Ordinal)
                    && obsAfter != obsBefore,
                    $"reObserved={obsAfter != obsBefore} result={(outcome ?? "(none)")[..Math.Min(70, (outcome ?? "(none)").Length)]}");

                // The blind-app fallback: a screenshot reaches the model as an image, and the log keeps only the
                // media id. This is what replaces OCR — the model reads the pixels itself. Waited for, because
                // the turn is still running when the approval lands.
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var hasImage = leaseLlm.Requests
                        .SelectMany(request => request.Messages)
                        .Any(message => message.Images is { Count: > 0 });

                    if (hasImage)
                    {
                        break;
                    }

                    await Task.Delay(25);
                }

                var shown = leaseLlm.Requests
                    .SelectMany(request => request.Messages)
                    .Where(message => message.Images is { Count: > 0 })
                    .ToList();

                var screenshotResult = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/session");
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    if (screenshotResult.GetProperty("items").EnumerateArray()
                        .Any(item => (item.GetProperty("body").GetString() ?? string.Empty).Contains("媒体 ", StringComparison.Ordinal)))
                    {
                        break;
                    }

                    await Task.Delay(25);
                    screenshotResult = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/session");
                }
                var shot = screenshotResult.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                    .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                    .LastOrDefault(body => body.Contains("媒体 ", StringComparison.Ordinal));

                // The turn keeps going after the screenshot: the bare-point move, then the point no window
                // covers. Waited for, because those results are the point of the check — and approved as they
                // arrive, because every desktop_act parks (the gear is on "all" in this workspace).
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var bodies = screenshotResult.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                        .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                        .ToList();

                    if (bodies.Any(body => body.StartsWith("MovePointer", StringComparison.Ordinal))
                        && bodies.Any(body => body.Contains("不落在唯一一个已观察窗口内", StringComparison.Ordinal)))
                    {
                        break;
                    }

                    foreach (var pending in (await leaseHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                        .GetProperty("pending").EnumerateArray().ToList())
                    {
                        await leaseHttp.PostAsJsonAsync(
                            $"/api/approvals/{pending.GetProperty("approvalId").GetString()}",
                            new { approved = true });
                    }

                    await Task.Delay(25);
                    screenshotResult = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/session");
                }

                var allResults = screenshotResult.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                    .Select(item => (item.GetProperty("body").GetString() ?? string.Empty).Replace("\n", " "))
                    .ToList();

                // The failure this check has to be diagnosable for: a lease that expired while the model was
                // thinking. The timestamps are what say whether the action was late or the lease was short.
                var timeline = string.Join(" | ", screenshotResult.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() is "ToolCall" or "ToolResult")
                    .Select(item => item.TryGetProperty("at", out var at)
                        ? $"{at.GetDateTime():HH:mm:ss.fff} {(item.GetProperty("body").GetString() ?? string.Empty)[..Math.Min(28, (item.GetProperty("body").GetString() ?? string.Empty).Length)]}"
                        : item.GetRawText()[..Math.Min(40, item.GetRawText().Length)]));

                Check("desktop: a screenshot reaches the model as an image",
                    shown.Count > 0 && shown[^1].Images!.Any(image => image.StartsWith("data:image/png;base64,", StringComparison.Ordinal)),
                    $"imageMessages={shown.Count} results=[{string.Join(" | ", allResults.Select(r => r[..Math.Min(60, r.Length)]))}]");

                Check("desktop: the log records the screenshot by media id, not by pixels",
                    shot is not null && !screenshotResult.GetRawText().Contains("iVBORw0KGgo", StringComparison.Ordinal),
                    $"result={(shot ?? "(none)")[..Math.Min(60, (shot ?? "(none)").Length)]}");

                // The call models actually make: coordinates, no window id. It used to be refused four times in
                // a row — "缺少 entityId" — and the turn died on the step limit with the app untouched.
                var barePoint = allResults.LastOrDefault(r => r.StartsWith("MovePointer", StringComparison.Ordinal));

                Check("desktop: a point with no window id resolves to the window that covers it",
                    barePoint is not null && barePoint.Contains("注入成功", StringComparison.Ordinal),
                    $"result={barePoint ?? "(none)"} | every result=["
                        + string.Join(" | ", allResults.Select(body => body[..Math.Min(70, body.Length)]))
                        + $"] | timeline=[{timeline}]");

                // And when no window covers the point, the refusal has to name the candidates — telling the model
                // to "observe first" when it has just observed is what produced the loop.
                var noWindow = allResults.LastOrDefault(r => r.Contains("不落在唯一一个已观察窗口内", StringComparison.Ordinal));

                Check("desktop: a point no window covers is refused with the candidates named",
                    noWindow is not null
                        && noWindow.Contains("entityId", StringComparison.Ordinal)
                        && noWindow.Contains("'", StringComparison.Ordinal),
                    $"result={(noWindow ?? "(none)")[..Math.Min(90, (noWindow ?? "(none)").Length)]}");

                // The telemetry aggregates: the turn above ran through observe → act → screenshot, so the
                // endpoint must hold at least one act and one picture — without those, a tuning decision has
                // no denominator. Verdicts are NOT required non-zero: the scripted turn refuses most acts
                // (guards) and the aim-only kinds no longer verify (they promise no picture), so every
                // verdict field can legitimately be 0 on a scripted turn.
                var telemetry = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");
                var hasAct = telemetry.GetProperty("acts").GetInt32() > 0;
                var hasPicture = telemetry.GetProperty("pictures").GetProperty("count").GetInt32() > 0;

                Check("desktop: telemetry aggregates the turn the model just ran",
                    hasAct && hasPicture,
                    $"acts={(telemetry.GetProperty("acts").GetInt32())} "
                        + $"pictures={(telemetry.GetProperty("pictures").GetProperty("count").GetInt32())} "
                        + $"verdicts={telemetry.GetProperty("verdicts").GetRawText()} "
                        + $"walkTruncated={telemetry.GetProperty("walkTruncated").GetInt32()}");
            }
            finally
            {
                await leaseApp.StopAsync();
                await leaseRun;
                await leaseVoice.DisposeAsync();
                await leaseConsumer.DisposeAsync();
                await leaseContext.DisposeAsync();
            }

            // ---- The continuing grant: approve once, the same window does not re-ask -------------
            var grantWorkspace = Path.Combine(root, "desktop-lease-grant");
            Directory.CreateDirectory(Path.Combine(grantWorkspace, "settings"));
            await File.WriteAllTextAsync(
                Path.Combine(grantWorkspace, "settings", "runtime.json"),
                """{"model":{"supportsImages":false},"desktop":{"enabled":true,"leaseTtlMs":15000,"allowedWindowTitles":[]}}""");

            var grantConsumerLlm = new LeaseThenActLlm();
            var grantPort = FreePort();
            var grantToken = TokenAuth.Generate();
            var grantClock = new OffsetClock(new DateTimeOffset(2026, 9, 27, 9, 30, 0, TimeSpan.Zero));
            var (grantApp, grantContext, grantConsumer, grantVoice, _) =
                await ApiHost.BuildAsync(grantWorkspace, grantPort, grantToken, grantClock, grantConsumerLlm);
            var grantRun = grantApp.RunAsync();
            using var grantHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{grantPort}") };
            grantHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grantToken);

            try
            {
                await WaitForHostAsync(grantHttp);
                await grantHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "连续操作一个窗口" });

                // The first act parks; approved here, which is where the continuing grant is minted —
                // not through any desktop endpoint, because a "yes" is the human's whole intent.
                string? grantApprovalId = null;
                for (var attempt = 0; attempt < 600 && grantApprovalId is null; attempt++)
                {
                    var pending = (await grantHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                        .GetProperty("pending").EnumerateArray().ToList();
                    if (pending.Count > 0)
                    {
                        grantApprovalId = pending[0].GetProperty("approvalId").GetString();
                    }

                    await Task.Delay(25);
                }
                var granted = grantApprovalId is not null
                    && (await grantHttp.PostAsJsonAsync($"/api/approvals/{grantApprovalId}", new { approved = true })).IsSuccessStatusCode;

                // The granted state must be server-visible, so a run and a restart see the same fact.
                TaskLeaseState? shownLease = null;
                for (var attempt = 0; attempt < 200 && shownLease is null; attempt++)
                {
                    shownLease = grantContext.TaskLeases.Snapshot().FirstOrDefault();
                    await Task.Delay(25);
                }

                Check("desktop: an approved action opens a continuing grant on its window",
                    granted && shownLease is not null
                    && shownLease.EntityId == PickTargetOf(grantConsumerLlm.Requests)
                    && shownLease.RemainingSteps == Api.Settings.TaskLeaseStore.DefaultSteps,
                    shownLease is null ? "no lease was minted" : $"entity={shownLease.EntityId} steps={shownLease.RemainingSteps}");

                // The second act on the same window is what the grant is for: it must not park, and it
                // must reach the tool (where the guard refuses it — the window is not in front), which
                // costs one of the ten.
                var secondResult = default(string?);
                var secondActParked = false;
                for (var attempt = 0; attempt < 400 && secondResult is null; attempt++)
                {
                    var pending = (await grantHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                        .GetProperty("pending").EnumerateArray().ToList();
                    if (pending.Count > 0 && pending[0].GetProperty("toolName").GetString() == "desktop_act")
                    {
                        // A parked second act is the failure this check exists to catch — deny it so the
                        // turn finishes, and report it precisely rather than as a timeout.
                        secondActParked = true;
                        secondResult = "(still parked: governance did not see the grant)";
                        await grantHttp.PostAsJsonAsync(
                            $"/api/approvals/{pending[0].GetProperty("approvalId").GetString()}",
                            new { approved = false });
                        break;
                    }

                    var polled = await grantHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    var results = polled.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                        .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                        .Where(body => body.Contains("不在前台", StringComparison.Ordinal))
                        .ToList();

                    // The first act's result is the guard refusal it earned the grant; find the second one.
                    if (results.Count >= 2)
                    {
                        secondResult = results[^1];
                        break;
                    }

                    await Task.Delay(25);
                }

                var spentLease = grantContext.TaskLeases.Snapshot().FirstOrDefault();
                Check("desktop: a granted window does not re-ask for the same kind",
                    secondResult is not null && !secondActParked
                    && spentLease is not null
                    && spentLease.RemainingSteps == Api.Settings.TaskLeaseStore.DefaultSteps - 1,
                    $"result={secondResult ?? "(timed out)"} "
                    + $"remaining={spentLease?.RemainingSteps.ToString() ?? "none"}");

                // What a grant must never cover: leaving the GUI parks even under a live grant.
                string? execApproval = null;
                for (var attempt = 0; attempt < 600 && execApproval is null; attempt++)
                {
                    var pending = (await grantHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                        .GetProperty("pending").EnumerateArray().ToList();
                    if (pending.Count > 0 && pending[0].GetProperty("toolName").GetString() == "desktop_exec")
                    {
                        execApproval = pending[0].GetProperty("approvalId").GetString();
                    }

                    await Task.Delay(25);
                }

                if (execApproval is not null)
                {
                    await grantHttp.PostAsJsonAsync($"/api/approvals/{execApproval}", new { approved = false });
                }

                // A denied exec is still “the tool could not run”: it settles without losing the grant.
                TaskLeaseState? afterDeny = null;
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await grantHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    if (polled.GetProperty("items").EnumerateArray()
                        .Any(item => (item.GetProperty("body").GetString() ?? string.Empty)
                            .Contains("denied:", StringComparison.Ordinal)))
                    {
                        afterDeny = grantContext.TaskLeases.Snapshot().FirstOrDefault();
                        break;
                    }

                    await Task.Delay(25);
                }

                Check("desktop: a grant never covers leaving the GUI even while one is live",
                    execApproval is not null
                    && afterDeny is not null
                    && afterDeny.RemainingSteps == Api.Settings.TaskLeaseStore.DefaultSteps - 1,
                    execApproval is null ? "desktop_exec never parked" : $"denied, grant steps={afterDeny?.RemainingSteps.ToString() ?? "(none)"}");
            }
            finally
            {
                await grantApp.StopAsync();
                await grantRun;
                await grantVoice.DisposeAsync();
                await grantConsumer.DisposeAsync();
                await grantContext.DisposeAsync();
            }

            var desktopWorkspace = Path.Combine(root, "desktop-tools");
            Directory.CreateDirectory(Path.Combine(desktopWorkspace, "settings"));
            await File.WriteAllTextAsync(
                Path.Combine(desktopWorkspace, "settings", "runtime.json"),
                """{"desktop":{"enabled":true,"leaseTtlMs":15000,"allowedWindowTitles":[]}}""");

            var desktopPort = FreePort();
            var desktopToken = TokenAuth.Generate();
            var desktopScripted = new ScriptedLlmClient(new[]
            {
                // Turn 1: look. Read-only, so it runs without approval.
                new LlmResponse(null, new[] { new LlmToolCall("c1", "desktop_observe", "{}") }, "tool_calls"),
                new LlmResponse("看过了。", Array.Empty<LlmToolCall>(), "stop"),
                // Turn 2: act. Irreversible, so it parks in the approval queue.
                new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c2", "desktop_act", """{"entityId":"deadbeef","kind":"LeftClick","x":1,"y":1}""") },
                    "tool_calls"),
                new LlmResponse("好吧。", Array.Empty<LlmToolCall>(), "stop"),
            });

            var (desktopApp, desktopContext, desktopConsumer, desktopVoice, _) =
                await ApiHost.BuildAsync(desktopWorkspace, desktopPort, desktopToken, clock, desktopScripted);
            var desktopRun = desktopApp.RunAsync();
            using var desktopHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{desktopPort}") };
            desktopHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", desktopToken);

            try
            {
                await WaitForHostAsync(desktopHttp);

                async Task<List<string>> ExposedToolsAsync()
                {
                    var consumer = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                    return consumer.GetProperty("tools").EnumerateArray()
                        .Select(tool => $"{tool.GetProperty("name").GetString()}:{tool.GetProperty("decision").GetString()}")
                        .ToList();
                }

                var gearAll = await ExposedToolsAsync();
                Check("desktop: the default gear asks for every action but never for looking",
                    gearAll.Contains("desktop_observe:allow")
                    && gearAll.Contains("desktop_act:ask")
                    && gearAll.Contains("desktop_exec:ask"),
                    string.Join(",", gearAll));

                await desktopHttp.PostAsJsonAsync("/api/desktop/approval", new { approval = "dangerous" });
                var gearDangerous = await ExposedToolsAsync();
                Check("desktop: the middle gear frees the GUI and still gates commands",
                    gearDangerous.Contains("desktop_act:allow")
                    && gearDangerous.Contains("desktop_exec:ask")
                    && gearDangerous.Contains("desktop_launch:ask"),
                    string.Join(",", gearDangerous));

                await desktopHttp.PostAsJsonAsync("/api/desktop/approval", new { approval = "none" });
                var gearNone = await ExposedToolsAsync();
                Check("desktop: the open gear asks for nothing",
                    gearNone.All(tool => tool.EndsWith(":allow", StringComparison.Ordinal)),
                    string.Join(",", gearNone));

                await desktopHttp.PostAsJsonAsync("/api/desktop/approval", new { approval = "all" });

                var state = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/desktop");
                Check("desktop: the capability reports itself enabled",
                    state.GetProperty("enabled").GetBoolean(),
                    $"enabled={state.GetProperty("enabled").GetBoolean()}");

                await desktopHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "看看桌面" });

                var observed = false;
                string? observeResult = null;
                for (var attempt = 0; attempt < 400 && !observed; attempt++)
                {
                    var polled = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    var results = polled.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                        .ToList();

                    if (results.Count > 0)
                    {
                        observeResult = results[^1].GetProperty("body").GetString();
                        observed = true;
                    }

                    await Task.Delay(25);
                }

                // The model's tool call reached the real desktop: the result names the windows it enumerated.
                Check("desktop: the model can observe through its tool",
                    observed
                    && observeResult is not null
                    && observeResult.Contains("\"obsId\"", StringComparison.Ordinal)
                    // The controls, not just the windows: addressing "the 播放 button" is the whole point.
                    && observeResult.Contains("\"elementId\"", StringComparison.Ordinal),
                    $"result={(observeResult ?? "(none)")[..Math.Min(80, (observeResult ?? "(none)").Length)]}");

                // Acting is irreversible, so governance parks it. The smoke answers — and denies.
                await desktopHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "点一下" });

                string? approvalId = null;
                for (var attempt = 0; attempt < 400 && approvalId is null; attempt++)
                {
                    var approvalQueue = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/approvals");
                    var pending = approvalQueue.GetProperty("pending").EnumerateArray().ToList();
                    if (pending.Count > 0)
                    {
                        approvalId = pending[0].GetProperty("approvalId").GetString();
                        Check("desktop: an action waits for approval instead of running",
                            pending[0].GetProperty("toolName").GetString() == "desktop_act",
                            pending[0].GetProperty("toolName").GetString() ?? "(none)");
                    }

                    await Task.Delay(25);
                }

                Check("desktop: the approval queue surfaces the parked action", approvalId is not null, $"id={approvalId}");

                if (approvalId is not null)
                {
                    var answered = await desktopHttp.PostAsJsonAsync(
                        $"/api/approvals/{approvalId}",
                        new { approved = false });

                    Check("desktop: answering the queue settles it", answered.IsSuccessStatusCode, $"status={answered.StatusCode}");

                    var denied = false;
                    string? denyResult = null;
                    for (var attempt = 0; attempt < 400 && !denied; attempt++)
                    {
                        var polled = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        var results = polled.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                            .ToList();

                        if (results.Count > 1)
                        {
                            denyResult = results[^1].GetProperty("body").GetString();
                            denied = denyResult is not null && denyResult.Contains("approval denied", StringComparison.Ordinal);
                        }

                        await Task.Delay(25);
                    }

                    // Nothing was injected: the refusal is what the model gets back, and the turn continues.
                    Check("desktop: a denied action is refused, not performed",
                        denied,
                        $"result={(denyResult ?? "(none)")[..Math.Min(90, (denyResult ?? "(none)").Length)]}");
                }

                var log = await ReadSharedAsync(Path.Combine(desktopWorkspace, "session", "events.jsonl"));
                Check("desktop: the refusal is in the log with the tool's name",
                    log.Contains("desktop_act", StringComparison.Ordinal)
                    && log.Contains("approval denied", StringComparison.Ordinal),
                    $"hasCall={log.Contains("\"desktop_act\"", StringComparison.Ordinal)}");
            }
            finally
            {
                await desktopApp.StopAsync();
                await desktopRun;
                await desktopVoice.DisposeAsync();
                await desktopConsumer.DisposeAsync();
                await desktopContext.DisposeAsync();
            }


            }
            finally
            {
                await wireApp.StopAsync();
                await wireRun;
                await wireVoice.DisposeAsync();
                await wireConsumer.DisposeAsync();
                await wireContext.DisposeAsync();
            }

            // Settings are the configuration surface, so pointing them at an endpoint must re-point the
            // running consumer — not just change what the UI displays.
            var settingsWorkspace = Path.Combine(root, "consumer-settings");
            var settingsPort = FreePort();
            var settingsToken = TokenAuth.Generate();
            var settingsModelPort = FreePort();

            using var settingsProvider = new FakeChatProvider(settingsModelPort, "由设置驱动的回复。");
            var (settingsApp, settingsContext, settingsConsumer, settingsVoice, _) =
                await ApiHost.BuildAsync(settingsWorkspace, settingsPort, settingsToken, clock);
            var settingsRun = settingsApp.RunAsync();
            using var settingsHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settingsPort}") };
            settingsHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settingsToken);

            try
            {
                await WaitForHostAsync(settingsHttp);

                var put = await settingsHttp.PutAsJsonAsync("/api/settings", new
                {
                    model = new
                    {
                        baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                        model = "settings-model",
                        apiKey = "smoke-key",
                        reasoningEffort = "XHIGH ",
                    },
                });
                var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
                var reconfigured = putBody.GetProperty("consumer");

                var delivered = await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "设置驱动" });
                var deliveredId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var settled = await WaitForQueueItemToSettleAsync(settingsHttp, deliveredId);

                var session = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var assistant = session.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() == "Assistant")
                    .Select(item => item.GetProperty("body").GetString())
                    .ToList();

                Check("settings: pointing the model settings at an endpoint re-points the consumer",
                    reconfigured.GetProperty("running").GetBoolean()
                    && reconfigured.GetProperty("model").GetString() == "settings-model"
                    && reconfigured.GetProperty("reason").GetString()!.Contains("设置", StringComparison.Ordinal)
                    && settled
                    && assistant.Count == 1
                    && assistant[0] == "由设置驱动的回复。"
                    && settingsProvider.SawAuthorization == "Bearer smoke-key",
                    $"running={reconfigured.GetProperty("running").GetBoolean()} settled={settled} reply={assistant.FirstOrDefault()}");

                // Discovery: the UI offers what the runtime can see rather than asking for names.
                var deviceList = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/voice/devices");
                var probe = await settingsHttp.PostAsJsonAsync("/api/models", new
                {
                    baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                });
                var probeBody = await probe.Content.ReadFromJsonAsync<JsonElement>();
                var probed = probeBody.GetProperty("models").EnumerateArray().Select(m => m.GetString()).ToList();

                Check("settings: the model listing is pulled through the API",
                    probed.SequenceEqual(new[] { "smoke-model-a", "smoke-model-b" })
                    && !probeBody.TryGetProperty("error", out _),
                    $"models={string.Join(",", probed)}");

                var dead = await settingsHttp.PostAsJsonAsync("/api/models", new
                {
                    baseUrl = $"http://127.0.0.1:{FreePort()}/v1",
                });
                var deadBody = await dead.Content.ReadFromJsonAsync<JsonElement>();

                Check("settings: an unreachable model endpoint returns a reason, not a silent empty list",
                    deadBody.GetProperty("models").GetArrayLength() == 0
                    && !string.IsNullOrWhiteSpace(deadBody.GetProperty("error").GetString()),
                    deadBody.GetProperty("error").GetString() ?? "(no reason)");

                // The failure this guards: a window containing hook injections and tool results used to be sent
                // as system messages in the middle of the conversation, which providers reject outright. The
                // first turn has an empty window, so only a second turn can catch it.
                var second = await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "第二轮" });
                var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var secondSettled = await WaitForQueueItemToSettleAsync(settingsHttp, secondId);

                var sent = settingsProvider.LastRequestBody ?? string.Empty;
                using var sentJson = JsonDocument.Parse(sent.Length == 0 ? "{}" : sent);
                var roles = sentJson.RootElement.TryGetProperty("messages", out var messages)
                    ? messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToList()
                    : new List<string>();

                Check("consumer: a second turn sends no system message after the first",
                    secondSettled && roles.Count > 0 && roles[0] == "system" && !roles.Skip(1).Contains("system"),
                    $"settled={secondSettled} bodyLen={sent.Length} path={settingsProvider.RequestPath} roles={string.Join(",", roles)}");

                // The gear travels settings → the client the consumer builds → the wire. A gear that is saved
                // and never sent is the kind of setting that looks configured and does nothing.
                var gear = sentJson.RootElement.TryGetProperty("reasoning_effort", out var sentGear)
                    ? sentGear.GetString()
                    : "(absent)";

                Check("settings: the thinking gear reaches the model request",
                    gear == "xhigh",
                    $"reasoning_effort={gear} bodyLen={sent.Length}");

                // The same gear, chosen for one message instead of for the runtime. This is the hop the
                // composer drives, and it is the one a settings-only check cannot see: the value has to
                // leave /api/input, ride the queue item, and win over the configured default on the wire.
                // The whole model section, not just the gear: a partial PUT replaces the record, so omitting
                // the endpoint here would blank it and fail validation instead of changing the menu.
                var perTurnPut = await settingsHttp.PutAsJsonAsync("/api/settings", new
                {
                    model = new
                    {
                        baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                        model = "settings-model",
                        // Carried over: the record is replaced wholesale, and the next check asserts the key
                        // is still stored but never echoed back.
                        apiKey = "smoke-key",
                        reasoningEffort = "low",
                        reasoningGears = new[] { "low", "scenic" },
                    },
                });
                await perTurnPut.Content.ReadAsStringAsync();

                var perTurn = await settingsHttp.PostAsJsonAsync("/api/input", new
                {
                    source = "text",
                    payload = "这一条换个档",
                    reasoningEffort = "scenic",
                });
                var perTurnId = (await perTurn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var perTurnSettled = await WaitForQueueItemToSettleAsync(settingsHttp, perTurnId);

                var perTurnBody = settingsProvider.LastRequestBody ?? string.Empty;
                using var perTurnJson = JsonDocument.Parse(perTurnBody.Length == 0 ? "{}" : perTurnBody);
                var perTurnGear = perTurnJson.RootElement.TryGetProperty("reasoning_effort", out var onWire)
                    ? onWire.GetString()
                    : "(absent)";

                Check("input: a gear chosen for one message wins over the configured default",
                    perTurnSettled && perTurnGear == "scenic",
                    $"settled={perTurnSettled} reasoning_effort={perTurnGear}");

                // A word the menu does not carry must not reach the model: the composer only ever offers
                // listed gears, so anything else is a stale client and the turn would silently differ.
                var offMenu = await settingsHttp.PostAsJsonAsync("/api/input", new
                {
                    source = "text",
                    payload = "这档不存在",
                    reasoningEffort = "turbo",
                });

                Check("input: a gear that is not on the menu is rejected and lists the ones that are",
                    offMenu.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && (await offMenu.Content.ReadAsStringAsync()).Contains("scenic", StringComparison.Ordinal),
                    $"status={(int)offMenu.StatusCode}");

                // The key went out on the wire but must never come back through the settings endpoint.
                var raw = await settingsHttp.GetStringAsync("/api/settings");
                Check("settings: the API key is sent to the model but never echoed back",
                    !raw.Contains("smoke-key", StringComparison.Ordinal)
                    && JsonDocument.Parse(raw).RootElement.GetProperty("model").GetProperty("apiKeySet").GetBoolean(),
                    "apiKeySet=true, value absent");

                // Removing history: the view and the log both lose it, and the conversation continues.
                var doomed = await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "要删掉的" });
                var doomedId = (await doomed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();
                await WaitForQueueItemToSettleAsync(settingsHttp, doomedId);

                var beforeRemoval = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var target = beforeRemoval.GetProperty("items").EnumerateArray()
                    .First(i => i.GetProperty("body").GetString() == "要删掉的")
                    .GetProperty("seq").GetInt64();

                // The log is JSON with CJK escaped, so it is parsed rather than searched: a raw Contains would
                // report "gone" for text that is still there.
                static bool LogHolds(string workspace, string body)
                {
                    var path = Path.Combine(workspace, "session", "events.jsonl");
                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (SessionJson.Deserialize(line)?.Body == body)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var delete = await settingsHttp.PostAsync($"/api/session/{target}/delete", content: null);
                var afterDelete = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var stillThere = afterDelete.GetProperty("items").EnumerateArray()
                    .Any(i => i.GetProperty("seq").GetInt64() == target);

                Check("session: deleting one event takes its text out of the log",
                    delete.IsSuccessStatusCode
                    && !stillThere
                    && !LogHolds(settingsWorkspace, "要删掉的")
                    && afterDelete.GetProperty("lastSeq").GetInt64() > beforeRemoval.GetProperty("lastSeq").GetInt64(),
                    $"deleted={delete.IsSuccessStatusCode} visible={stillThere} "
                    + $"inLog={LogHolds(settingsWorkspace, "要删掉的")} "
                    + $"lastSeq {beforeRemoval.GetProperty("lastSeq").GetInt64()}->{afterDelete.GetProperty("lastSeq").GetInt64()}");

                // Retraction takes everything from the selected event onward, as of now.
                var keptItem = afterDelete.GetProperty("items").EnumerateArray().First();
                var kept = keptItem.GetProperty("seq").GetInt64();
                var keptBody = keptItem.GetProperty("body").GetString() ?? string.Empty;
                var retract = await settingsHttp.PostAsync($"/api/session/{kept}/retract", content: null);
                var afterRetract = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");

                Check("session: retracting removes the selected event and takes its text out of the log",
                    retract.IsSuccessStatusCode
                    && afterRetract.GetProperty("items").GetArrayLength() == 0
                    && !LogHolds(settingsWorkspace, keptBody),
                    $"retracted={retract.IsSuccessStatusCode} "
                    + $"remaining={afterRetract.GetProperty("items").GetArrayLength()} "
                    + $"inLog={LogHolds(settingsWorkspace, keptBody)}");

                // And the conversation continues: a closed range must not swallow what comes next.
                await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "撤回之后" });
                var revived = false;
                for (var attempt = 0; attempt < 200 && !revived; attempt++)
                {
                    var polled = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    revived = polled.GetProperty("items").EnumerateArray().Any(i =>
                        i.GetProperty("body").GetString() == "撤回之后");
                    await Task.Delay(25);
                }

                Check("session: events after a retraction stay visible", revived, $"revived={revived}");

                // Shrinking the context folds history once, using the model that is already configured. Enough
                // history is needed first: folding one event is not compaction. The wait is on the session's own
                // sequence — a turn that is mid-flight is not in the history yet.
                // Counted in content events, not in seq: a turn also appends turnStart/stepStart/stepEnd/turnEnd,
                // so waiting for "six more seqs" can be satisfied by the control events of one turn while the
                // other two are still running — and then the shrink happens before there is history to fold.
                static int ContentCount(JsonElement session) => session.GetProperty("items").EnumerateArray()
                    .Count(item => item.GetProperty("kind").GetString() is "User" or "Assistant");

                var fillContent = ContentCount(await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session"));

                for (var i = 0; i < 3; i++)
                {
                    await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = $"填充 {i}" });
                }

                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    if (ContentCount(polled) >= fillContent + 6)
                    {
                        break;
                    }

                    await Task.Delay(25);
                }

                // The context meter: the occupancy reported to the UI is the one that decides folding. Measured
                // against the roomy default window, where another turn is a visible increase. The wait is on the
                // session itself — queue depth can read zero while a turn is mid-flight, and then the reply this
                // check is about has not been written yet.
                var usageView = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var usageBefore = usageView.GetProperty("context");
                var seqBefore = usageView.GetProperty("lastSeq").GetInt64();

                await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "再填充一些内容让占用增长" });

                var usageAfter = usageBefore;
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    if (polled.GetProperty("lastSeq").GetInt64() >= seqBefore + 2)
                    {
                        usageAfter = polled.GetProperty("context");
                        break;
                    }

                    await Task.Delay(25);
                }

                Check("context: the reported occupancy follows the history",
                    usageAfter.GetProperty("usedTokens").GetInt32() > usageBefore.GetProperty("usedTokens").GetInt32()
                    && usageAfter.GetProperty("usedTokens").GetInt32() <= usageAfter.GetProperty("budgetTokens").GetInt32(),
                    $"{usageBefore.GetProperty("usedTokens").GetInt32()} -> {usageAfter.GetProperty("usedTokens").GetInt32()} "
                    + $"budget={usageAfter.GetProperty("budgetTokens").GetInt32()}");

                var beforeShrink = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var shrink = await settingsHttp.PutAsJsonAsync("/api/settings", new
                {
                    model = new
                    {
                        baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                        model = "settings-model",
                        // ~5 tokens per short turn: a 16-token window with a 4-token reserve keeps two.
                        contextTokens = 16,
                        maxTokens = 4,
                    },
                });
                var shrinkBody = (await shrink.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("compaction");

                Check("settings: shrinking the context compacts the history once",
                    shrinkBody.GetProperty("compacted").GetBoolean()
                    && shrinkBody.GetProperty("foldedEvents").GetInt32() > 0,
                    $"items={beforeShrink.GetProperty("items").GetArrayLength()} "
                    + $"{shrinkBody.GetProperty("reason").GetString() ?? "(no reason)"}");

                var compacted = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var compactedUsage = compacted.GetProperty("context");
                var summaries = compacted.GetProperty("items").EnumerateArray()
                    .Count(i => i.GetProperty("kind").GetString() == "Summary");

                // After folding, the meter must show the folded history rather than the raw log. The window here
                // is deliberately tiny (16 tokens), so the always-sent system block alone can exceed the budget —
                // what matters is that folding lowered the prompt, not that it fits this artificial window.
                Check("context: the meter reflects the folding",
                    compactedUsage.GetProperty("foldedItems").GetInt32() > 0
                    && compactedUsage.GetProperty("usedTokens").GetInt32() < usageAfter.GetProperty("usedTokens").GetInt32(),
                    $"folded={compactedUsage.GetProperty("foldedItems").GetInt32()} "
                    + $"{usageAfter.GetProperty("usedTokens").GetInt32()} -> {compactedUsage.GetProperty("usedTokens").GetInt32()} "
                    + $"budget={compactedUsage.GetProperty("budgetTokens").GetInt32()}");

                Check("context: the budget is the window minus the reserved output",
                    compactedUsage.GetProperty("budgetTokens").GetInt32()
                    == compactedUsage.GetProperty("contextTokens").GetInt32() - compactedUsage.GetProperty("maxTokens").GetInt32(),
                    $"{compactedUsage.GetProperty("contextTokens").GetInt32()} - {compactedUsage.GetProperty("maxTokens").GetInt32()}");

                Check("settings: the compaction leaves exactly one summary standing in for the history",
                    summaries == 1
                    && compacted.GetProperty("items").GetArrayLength() < beforeShrink.GetProperty("items").GetArrayLength(),
                    $"summaries={summaries} items={beforeShrink.GetProperty("items").GetArrayLength()}"
                    + $"->{compacted.GetProperty("items").GetArrayLength()}");
            }
            finally
            {
                await settingsApp.StopAsync();
                await settingsRun;
                await settingsVoice.DisposeAsync();
                await settingsConsumer.DisposeAsync();
                await settingsContext.DisposeAsync();
            }

            // The whole product loop in one place: speak → queue → turn → session → speech.
            var loopWorkspace = Path.Combine(root, "voice-loop");
            var loopPort = FreePort();
            var loopToken = TokenAuth.Generate();
            var components = new SyntheticVoiceComponents("打开书房的灯");

            var loopLlm = new ScriptedLlmClient(new[]
            {
                new LlmResponse("书房的灯已经打开了。窗帘也拉上了。", Array.Empty<LlmToolCall>(), "stop"),
            });

            var (loopApp, loopContext, loopConsumer, loopVoice, _) =
                await ApiHost.BuildAsync(loopWorkspace, loopPort, loopToken, clock, loopLlm, components);
            var loopRun = loopApp.RunAsync();
            using var loopHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{loopPort}") };
            loopHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loopToken);

            try
            {
                await WaitForHostAsync(loopHttp);

                var enable = await loopHttp.PutAsJsonAsync("/api/settings", new
                {
                    input = new { enabled = true, utteranceGapMs = 200, silenceThreshold = 0.03 },
                    output = new { enabled = true },
                    asr = new { baseUrl = "http://127.0.0.1:45101", model = "smoke-asr" },
                    tts = new { baseUrl = "http://127.0.0.1:45102", lang = "zh" },
                });
                enable.EnsureSuccessStatusCode();
                var voiceState = (await enable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("voice");

                Check("voice: enabling the halves starts them on the configured devices",
                    voiceState.GetProperty("input").GetProperty("running").GetBoolean()
                    && voiceState.GetProperty("output").GetProperty("running").GetBoolean()
                    && voiceState.GetProperty("input").GetProperty("device").GetString() == "synthetic-mic",
                    $"in={voiceState.GetProperty("input").GetProperty("device").GetString()} "
                    + $"out={voiceState.GetProperty("output").GetProperty("device").GetString()}");

                // Speak 0.6 s, then 1.4 s of silence: one utterance, on a stream-like cadence.
                var source = components.LastSource!;
                for (var i = 0; i < 30; i++)
                {
                    source.Emit(0.5f, 320);
                    await Task.Delay(25);
                }

                for (var i = 0; i < 70; i++)
                {
                    source.Emit(0f, 320);
                    await Task.Delay(25);
                }

                // Capture → queue → turn → session → speech.
                var answered = false;
                var spoken = false;
                for (var attempt = 0; attempt < 400 && !(answered && spoken); attempt++)
                {
                    if (!answered)
                    {
                        var session = await loopHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        answered = session.GetProperty("items").EnumerateArray().Any(item =>
                            item.GetProperty("kind").GetString() == "Assistant"
                            && item.GetProperty("body").GetString()!.Contains("书房的灯", StringComparison.Ordinal));
                    }

                    spoken = components.TtsRequests.Count >= 2;
                    await Task.Delay(25);
                }

                Check("voice: an utterance is answered by a turn and the answer is spoken",
                    answered && spoken && components.LastSink!.Segments.Count > 0,
                    $"answered={answered} sentences={components.TtsRequests.Count} segments={components.LastSink?.Segments.Count ?? 0}");

                var loopQueue = await loopHttp.GetFromJsonAsync<JsonElement>("/api/queue");
                var inputState = await loopHttp.GetFromJsonAsync<JsonElement>("/api/voice");
                Check("voice: the utterance is consumed and the halves stay up",
                    loopQueue.GetProperty("depth").GetInt32() == 0
                    && inputState.GetProperty("input").GetProperty("count").GetInt32() == 1
                    && inputState.GetProperty("output").GetProperty("count").GetInt32() >= 2,
                    $"depth={loopQueue.GetProperty("depth").GetInt32()} "
                    + $"utterances={inputState.GetProperty("input").GetProperty("count").GetInt32()} "
                    + $"spoken={inputState.GetProperty("output").GetProperty("count").GetInt32()}");

                // A device that refuses to open must not take the other half down with it. The output section
                // changes (drainMs), because an unchanged section is deliberately not restarted.
                components.FailToOpenSink = true;
                var reenable = await loopHttp.PutAsJsonAsync("/api/settings", new
                {
                    input = new { enabled = true, utteranceGapMs = 200 },
                    output = new { enabled = true, drainMs = 123 },
                    asr = new { baseUrl = "http://127.0.0.1:45101", model = "smoke-asr" },
                    tts = new { baseUrl = "http://127.0.0.1:45102", lang = "zh" },
                });
                var afterFailure = (await reenable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("voice");

                Check("voice: a device that will not open is reported without stopping the other half",
                    afterFailure.GetProperty("input").GetProperty("running").GetBoolean()
                    && !afterFailure.GetProperty("output").GetProperty("running").GetBoolean()
                    && afterFailure.GetProperty("output").GetProperty("reason").GetString()!.Contains("打开输出设备失败", StringComparison.Ordinal),
                    afterFailure.GetProperty("output").GetProperty("reason").GetString() ?? "(none)");
            }
            finally
            {
                await loopApp.StopAsync();
                await loopRun;
                await loopVoice.DisposeAsync();
                await loopConsumer.DisposeAsync();
                await loopContext.DisposeAsync();
            }
        }

        // ---- 16. the minimized-window checks, last on purpose ----
        await MinimizedWindowChecksAsync(root);
    }

    /// <summary>
    /// Creates the state under test. Nothing in the product minimizes a window — the observation, the picture
    /// and the click guard only have to cope with the state — so the smoke produces it, and the check focuses
    /// the window afterwards to put the desktop back.
    /// </summary>
    /// <summary>
    /// Everything about a minimized window: is it reported as one, is it refused as a picture, is it refused
    /// as a click target, and can a person get it back. The last two need a real window with a message pump and
    /// a session that will grant foreground, so they are opt-in (PARTNER_DESKTOP_LIVE=1); the first three act
    /// on a window this run created.
    /// </summary>
    private static async Task MinimizedWindowChecksAsync(string root)
    {
        using (var shotAutomation = new DesktopAutomation())
        using (var owned = OwnedWindow.Create("Partner smoke — minimized window probe"))
        {
            var probe = owned.Created
                ? shotAutomation.Observe().Entities.FirstOrDefault(entity => entity.Label.Contains("Partner smoke", StringComparison.Ordinal))
                : null;

            if (probe is null)
            {
                var why = owned.Failure ?? "the probe window did not appear in the observation";
                Skip("desktop: a minimized window is reported as minimized rather than as a small window", why);
                Skip("desktop: a minimized window is refused as a picture instead of a 160×28 stub", why);
                Skip("desktop: a click into a minimized window's placeholder rect is refused", why);
            }
            else
            {
                var handle = nint.Parse(probe.EntityId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                WindowProbe.Minimize(handle);
                await Task.Delay(700);

                var minimized = shotAutomation.Observe().Find(probe.EntityId);

                Check("desktop: a minimized window is reported as minimized rather than as a small window",
                    minimized is { IsMinimized: true },
                    $"minimized={minimized?.IsMinimized} rect={minimized?.Rect.X},{minimized?.Rect.Y} {minimized?.Rect.Width}×{minimized?.Rect.Height}");

                if (minimized is not null)
                {
                    var refused = await new DesktopScreenshotTool(shotAutomation, new FileMediaStore(Path.Combine(root, "media")))
                        .InvokeAsync($"{{\"entityId\":\"{minimized.EntityId}\"}}");

                    Check("desktop: a minimized window is refused as a picture instead of a 160×28 stub",
                        !refused.Ok && refused.Content.Contains("已最小化", StringComparison.Ordinal),
                        refused.Content);

                    var blocked = DesktopActTool.Guard(
                        ActionKind.LeftClick,
                        minimized,
                        (minimized.Rect.X + 5, minimized.Rect.Y + 5),
                        new DesktopActTool.DesktopActArguments(
                            null, null, "LeftClick", minimized.Rect.X + 5, minimized.Rect.Y + 5, null, null, null, null, null));

                    Check("desktop: a click into a minimized window's placeholder rect is refused",
                        blocked is not null && blocked.Contains("已最小化", StringComparison.Ordinal),
                        blocked ?? "(none)");
                }
            }

            // ---- the two checks that need a real window ----
            if (!LiveDesktop)
            {
                Skip("desktop: focusing a minimized window restores it instead of only lighting up the taskbar",
                    "PARTNER_DESKTOP_LIVE is not 1 (focusing a window takes the reader's focus)");
                Skip("desktop: a restored window is screenshot from the stale observation instead of being refused",
                    "PARTNER_DESKTOP_LIVE is not 1 (needs the focus check above, and a window with a message pump)");
            }
            else
            {
                // A real window, and never the shell's own: Program Manager cannot be minimized, so the check
                // would then be about nothing. The focus is handed back at the end.
                var all = shotAutomation.Observe().Entities;
                var original = all.FirstOrDefault(entity => entity.IsForeground && entity.Label != "Program Manager");
                var live = original ?? all.FirstOrDefault(entity => !entity.IsMinimized && entity.Label != "Program Manager");

                if (live is null)
                {
                    var why = "no window on this desktop can be minimized";
                    Skip("desktop: focusing a minimized window restores it instead of only lighting up the taskbar", why);
                    Skip("desktop: a restored window is screenshot from the stale observation instead of being refused", why);
                }
                else
                {
                    var liveHandle = nint.Parse(live.EntityId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                    WindowProbe.Minimize(liveHandle);
                    await Task.Delay(700);

                    var stale = shotAutomation.Observe().Find(live.EntityId);

                    if (stale is null)
                    {
                        var why = $"'{live.Label}' vanished after being minimized";
                        Skip("desktop: focusing a minimized window restores it instead of only lighting up the taskbar", why);
                        Skip("desktop: a restored window is screenshot from the stale observation instead of being refused", why);
                    }
                    else
                    {
                        // Focus is what a person does to a minimized window, and it has to mean the same thing here.
                        var focused = await shotAutomation.ActAsync(stale.EntityId, ActionKind.Focus, new ActionParams());
                        await Task.Delay(700);

                        var restored = shotAutomation.Observe().Find(stale.EntityId);

                        Check("desktop: focusing a minimized window restores it instead of only lighting up the taskbar",
                            focused.Injection?.Ok == true && restored is { IsMinimized: false } && restored.Rect.Y > -32000,
                            $"injection={focused.Injection?.Detail} win32={focused.Injection?.ErrorCode} minimized={restored?.IsMinimized} rect={restored?.Rect.X},{restored?.Rect.Y}");

                        // The log's next trap: the picture was refused from the observation taken *before* the
                        // focus, so a restore that had already happened looked like it had not. The tool has to
                        // ask the window.
                        var afterRestore = await new DesktopScreenshotTool(shotAutomation, new FileMediaStore(Path.Combine(root, "media")))
                            .InvokeAsync($"{{\"entityId\":\"{stale.EntityId}\"}}");

                        Check("desktop: a restored window is screenshot from the stale observation instead of being refused",
                            afterRestore.Ok && afterRestore.Images is { Count: 1 },
                            afterRestore.Content[..Math.Min(80, afterRestore.Content.Length)]);

                        if (original is not null)
                        {
                            await shotAutomation.ActAsync(original.EntityId, ActionKind.Focus, new ActionParams());
                        }
                    }
                }
            }
        }
    }

    private static class WindowProbe
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint handle, int command);

        /// <summary>SW_MINIMIZE.</summary>
        public static void Minimize(nint handle) => ShowWindow(handle, 6);
    }

/// <summary>
    /// A window the smoke run owns, so the minimized-window checks never touch the reader's own.
    /// <para>
    /// The state has to be produced — nothing in the product minimizes a window — and it used to be produced
    /// on whatever was in front, which made the result a property of the machine rather than of the code: a
    /// fullscreen window takes the focus back the moment it is minimized, and the check reported the product
    /// broken on a desktop where nothing had changed.
    /// </para>
    /// <para>
    /// It lives on a thread of its own with a real message pump, and both halves of that are load-bearing.
    /// Without the pump, capturing the window kills it — a GDI capture sends messages to the window's own
    /// thread and nobody is there to answer, so what comes back is a dead handle rather than pixels. Without
    /// the dedicated thread, the window belongs to whichever thread happened to call in, which here is a
    /// thread-pool thread: the runtime retires those, and it takes every window they created with it, at a
    /// moment of its choosing.
    /// </para>
    /// </summary>
    private sealed class OwnedWindow : IDisposable
    {
        private const string ClassName = "PartnerSmokeProbeWindow";
        private const uint WmClose = 0x0010;
        private const uint WmQuit = 0x0012;
        private const uint ErrorClassAlreadyExists = 1410;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")]
        private static extern ushort RegisterClass(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern nint CreateWindowExW(
            uint extended, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint handle);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandleW(string? module);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostThreadMessageW")]
        private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            // WNDCLASSW, not WNDCLASSEXW: RegisterClass (not RegisterClassEx) takes no cbSize and no atom,
            // and getting that layout wrong is ERROR_INVALID_PARAMETER rather than anything readable.
            public uint Style;
            public nint WindowProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public nint Window;
            public uint Value;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public int PointX;
            public int PointY;
        }

        /// <summary>The thread's message pump, kept as a delegate so the window's procedure stays callable.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);

        private static readonly WindowProcedure Procedure = DefWindowProc;

        private readonly ManualResetEventSlim _ready = new(false);
        private Thread? _thread;

        public nint Handle { get; private set; }

        /// <summary>False when the session will not hand this run a window (a locked-down desktop returns none).</summary>
        public bool Created { get; private set; }

        /// <summary>Why the window is absent, when it is — a skip nobody can act on is a skip that gets ignored.</summary>
        public string? Failure { get; private set; }

        /// <summary>
        /// A visible, ordinary, titled window: big enough that Windows gives it a real placement rather than
        /// the 160×28 taskbar stub, and empty so there is nothing to look at.
        /// </summary>
        public static OwnedWindow Create(string title)
        {
            var window = new OwnedWindow();
            nint handle = 0;
            string? failure = null;

            var thread = new Thread(() =>
            {
                var instance = GetModuleHandleW(null);
                var windowClass = new WindowClass
                {
                    Name = ClassName,
                    WindowProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                    Instance = instance,
                    // A plain button face: an empty window is fine, an invisible one would not be findable.
                    Background = 5,
                };

                if (RegisterClass(ref windowClass) == 0 && GetLastError() != ErrorClassAlreadyExists)
                {
                    failure = $"RegisterClassW failed (win32 {GetLastError()})";
                    window._ready.Set();
                    return;
                }

                // WS_OVERLAPPEDWINDOW | WS_VISIBLE.
                const uint style = 0x00CF0000 | 0x10000000;
                handle = CreateWindowExW(0, ClassName, title, style, 120, 120, 480, 240, 0, 0, instance, 0);
                if (handle == 0)
                {
                    failure = $"CreateWindowExW failed (win32 {GetLastError()})";
                    window._ready.Set();
                    return;
                }

                window._ready.Set();
                Pump(handle);
            })
            {
                IsBackground = true,
                Name = "partner-smoke-window",
            };

            window._thread = thread;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // Five seconds is generous for a window that either appears or does not; the alternative is a
            // check that hangs on a locked-down session.
            if (!window._ready.Wait(TimeSpan.FromSeconds(5)))
            {
                failure ??= "the window thread did not answer";
            }

            window.Handle = handle;
            window.Created = handle != 0;
            window.Failure = failure;
            return window;
        }

        /// <summary>
        /// The pump. A close is how this run ends the window; anything else goes to DefWindowProc, which is
        /// all a bare window needs. It exits on WM_QUIT, which <see cref="Dispose"/> posts by thread id.
        /// </summary>
        private static void Pump(nint window)
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Value == WmClose)
                {
                    DestroyWindow(message.Window);
                    continue;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(ref NativeMessage message);

        public void Dispose()
        {
            if (Handle != 0)
            {
                PostMessage(Handle, WmClose, 0, 0);
            }

            // WM_QUIT is thread-directed, so it cannot ride along on the window's queue.
            if (_thread is not null)
            {
                PostThreadMessage((uint)_thread.ManagedThreadId, WmQuit, 0, 0);
                _thread.Join(TimeSpan.FromSeconds(2));
            }

            _ready.Dispose();
        }
    }

    /// <summary>Synthetic voice components: the whole pipeline without a microphone, a speaker or a service.</summary>
    private sealed class SyntheticVoiceComponents(string transcript) : IVoiceComponents
    {
        public SyntheticAudioSource? LastSource { get; private set; }

        public SyntheticAudioSink? LastSink { get; private set; }

        public List<string> TtsRequests { get; } = new();

        public bool FailToOpenSink { get; set; }

        public IAudioSource OpenSource(VoiceInputSetting setting)
        {
            LastSource = new SyntheticAudioSource(16_000, "synthetic-mic");
            return LastSource;
        }

        public IAudioSink OpenSink(VoiceOutputSetting setting)
        {
            if (FailToOpenSink)
            {
                throw new InvalidOperationException("smoke: simulated device failure");
            }

            LastSink = new SyntheticAudioSink(setting.SampleRate, "synthetic-speaker");
            return LastSink;
        }

        public IReadOnlyList<string> ListInputDevices() => new[] { "synthetic-mic" };

        public IReadOnlyList<string> ListOutputDevices() => new[] { "synthetic-speaker" };

        public IAsrClient CreateAsr(AsrOptions options) => new ScriptedAsr(transcript);

        public ITtsClient CreateTts(TtsOptions options) => new ScriptedTts(TtsRequests);

        private sealed class ScriptedAsr(string text) : IAsrClient
        {
            public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default) => Task.FromResult(text);

            public async IAsyncEnumerable<string> TranscribeStreamAsync(
                float[] pcm,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return text;
                await Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private sealed class ScriptedTts(List<string> requests) : ITtsClient
        {
            public async IAsyncEnumerable<float[]> SynthesizeAsync(
                string text,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                requests.Add(text);
                yield return new float[2400];
                await Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// A minimal OpenAI-compatible endpoint over a raw socket: enough to prove the production client works
    /// against a real HTTP hop, without depending on a provider being installed on this machine.
    /// </summary>
    private sealed class FakeChatProvider : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _reply;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeChatProvider(int port, string reply)
        {
            _reply = reply;
            _listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public string? RequestPath { get; private set; }

        public string? SawAuthorization { get; private set; }

        /// <summary>The last request body, so the message shape sent to a provider can be asserted.</summary>
        public string? LastRequestBody { get; private set; }

        /// <summary>Every request line seen, in order — the only way to see where a stuck turn stopped.</summary>
        public List<string> Requests { get; } = new();

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                using (client)
                {
                    await AnswerAsync(client).ConfigureAwait(false);
                }
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            var stream = client.GetStream();

            // Read the request framing byte by byte, deliberately without a StreamReader: a buffered reader
            // pre-reads into its own buffer, so a later read straight from the stream would wait forever for
            // bytes the reader had already swallowed. Content-Length also counts bytes, not characters.
            var header = new List<byte>();
            var single = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(single);
                if (read == 0)
                {
                    return;
                }

                header.Add(single[0]);

                if (header.Count >= 4
                    && header[^4] == (byte)'\r' && header[^3] == (byte)'\n'
                    && header[^2] == (byte)'\r' && header[^1] == (byte)'\n')
                {
                    break;
                }
            }

            var lines = System.Text.Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            RequestPath = lines[0];
            Requests.Add(RequestPath);

            var length = 0;

            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    SawAuthorization = line["Authorization:".Length..].Trim();
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            var body = new byte[length];
            var filled = 0;
            while (filled < length)
            {
                var chunk = await stream.ReadAsync(body.AsMemory(filled, length - filled));
                if (chunk == 0)
                {
                    break;
                }

                filled += chunk;
            }

            LastRequestBody = System.Text.Encoding.UTF8.GetString(body, 0, filled);

            // A streamed completion is answered frame by frame with a pause between them, so the session view
            // can be observed while the reply is still arriving. No Content-Length: SSE ends at EOF, which is
            // also what lets each frame out before the next one exists.
            if (LastRequestBody.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                await AnswerStreamAsync(stream).ConfigureAwait(false);
                return;
            }

            // One socket serves both the completion and the listing, so the proxy is exercised for real.
            var payload = RequestPath?.Contains("/models", StringComparison.Ordinal) == true
                ? "{\"data\":[{\"id\":\"smoke-model-a\"},{\"id\":\"smoke-model-b\"}]}"
                : RequestPath?.Contains("/tokenize", StringComparison.Ordinal) == true
                    ? "{\"tokens\":[1,2,3,4,5,6,7]}"
                : System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new { role = "assistant", content = _reply },
                            finish_reason = "stop",
                        },
                    },
                });

            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            var head = System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(head);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Answers a streamed completion as SSE: one frame per piece, released with a pause between them.
        /// <para>
        /// The pauses are the point — they are what makes "the reply is visible before the turn ends"
        /// observable rather than a claim about code that merely parses frames quickly.
        /// </para>
        /// </summary>
        private async Task AnswerStreamAsync(NetworkStream stream)
        {
            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            // The provider's own reply, in pieces: the concatenation is what the buffered path would have
            // returned, so the checks that assert the reply text are asserting the same answer.
            const int Chunk = 2;

            for (var i = 0; i < _reply.Length; i += Chunk)
            {
                var piece = _reply.Substring(i, Math.Min(Chunk, _reply.Length - i));
                var frame = System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = piece } } },
                });

                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"data: {frame}\n\n")).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);

                // Long enough for a poll to land between two pieces, short enough not to slow the suite down.
                await Task.Delay(60).ConfigureAwait(false);
            }

            var finish = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { }, finish_reason = "stop" } },
            });

            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"data: {finish}\n\n")).ConfigureAwait(false);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: [DONE]\n\n")).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();

            try
            {
                _serving.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The listener was stopped mid-accept; nothing to report.
            }

            _stop.Dispose();
        }
    }

    /// <summary>
    /// A model that observes and then acts on what it saw: the entity id of a real window is machine-specific,
    /// so a fixed script cannot name one. It picks a window that is *not* in front, which makes the keystroke a
    /// refusal the guards produce — the test never types into the user's desktop.
    /// </summary>
    private sealed class ObserveThenActLlm : ILlmClient
    {
        private readonly List<LlmRequest> _requests = new();

        public IReadOnlyList<LlmRequest> Requests => _requests;

        /// <summary>
        /// The ReAct shape, driven by what is already in the conversation: look, act on what was seen, then
        /// answer. The entity id of a real window is machine-specific, so a fixed script cannot name one — and
        /// the target is chosen to be a window that is *not* in front, which makes the keystroke a refusal the
        /// guards produce rather than something typed into the user's desktop.
        /// </summary>
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            _requests.Add(request);

            var toolResults = request.Messages.Count(message => message.Role == "tool");

            return Task.FromResult(toolResults switch
            {
                0 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c1", "desktop_observe", "{}") },
                    "tool_calls"),
                1 => new LlmResponse(
                    null,
                    new[]
                    {
                        new LlmToolCall(
                            "c2",
                            "desktop_act",
                            $"{{ \"entityId\": \"{PickTarget()}\", \"kind\": \"KeyPress\", \"virtualKey\": 67 }}"),
                    },
                    "tool_calls"),
                2 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c3", "desktop_screenshot", $"{{ \"entityId\": \"{PickTarget()}\" }}") },
                    "tool_calls"),
                // What models actually do after looking: click the coordinates, with no window id. A pointer move
                // rather than a click, so the check never presses anything on the user's desktop.
                3 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c4", "desktop_act", $"{{ \"kind\": \"MovePointer\", \"x\": {PickCentre().X}, \"y\": {PickCentre().Y} }}") },
                    "tool_calls"),
                // A point no window covers: the refusal has to say which windows exist, or the model can only
                // repeat the same call — which is exactly what it did, until the turn hit the step limit.
                4 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c5", "desktop_act", "{ \"kind\": \"MovePointer\", \"x\": -100000, \"y\": -100000 }") },
                    "tool_calls"),
                _ => new LlmResponse("好吧。", Array.Empty<LlmToolCall>(), "stop"),
            });
        }

        /// <summary>The centre of the picked window, in screen coordinates, as desktop_observe reports them.</summary>
        private (int X, int Y) PickCentre() => PickCentreOf(_requests);

        /// <summary>The window the observation listed, preferring one that is not foreground.</summary>
        private string PickTarget() => PickTargetOf(_requests);

        public Task<LlmResponse> StreamAsync(LlmRequest request, Action<string> onText, CancellationToken ct = default) =>
            CompleteAsync(request, ct);
    }

    /// <summary>The window the observation listed, preferring one that is not foreground: the entity id of a
    /// real window is machine-specific, so any scripted model has to read what it just observed.</summary>
    private static string PickTargetOf(IEnumerable<LlmRequest> requests)
    {
        var observed = requests
            .SelectMany(request => request.Messages)
            .Where(message => message.Role == "tool" && message.Content is not null)
            .Select(message => message.Content!)
            .LastOrDefault(content => content.Contains("\"entities\"", StringComparison.Ordinal));

        if (observed is null)
        {
            return "1";
        }

        // The observation now carries a trailing note (the blind-Electron hint) after the JSON; the pick
        // reads only the JSON document, which ends at the last '}' before the note.
        var jsonEnd = observed.LastIndexOf('}');
        using var document = JsonDocument.Parse(observed[..(jsonEnd + 1)]);
        var entities = document.RootElement.GetProperty("entities").EnumerateArray()
            // A minimized window is a 160×28 stub at -32000: nothing can be clicked or seen in it, so a
            // model that reads the observation picks a real one.
            .Where(entity => !(entity.TryGetProperty("minimized", out var small) && small.GetBoolean()))
            .ToList();

        if (entities.Count == 0)
        {
            return "1";
        }

        var background = entities.FirstOrDefault(entity =>
            !entity.TryGetProperty("foreground", out var flag) || !flag.GetBoolean());

        return (background.ValueKind == JsonValueKind.Undefined ? entities[0] : background)
            .GetProperty("entityId").GetString() ?? "1";
    }

    /// <summary>The centre of the picked window, in screen coordinates, as desktop_observe reports them.</summary>
    private static (int X, int Y) PickCentreOf(IEnumerable<LlmRequest> requests)
    {
        var target = PickTargetOf(requests);
        var observed = requests
            .SelectMany(request => request.Messages)
            .Where(message => message.Role == "tool" && message.Content is not null)
            .Select(message => message.Content!)
            .LastOrDefault(content => content.Contains("\"entities\"", StringComparison.Ordinal));

        if (observed is null)
        {
            return (1, 1);
        }

        using var document = JsonDocument.Parse(observed[..(observed.LastIndexOf('}') + 1)]);
        var entity = document.RootElement.GetProperty("entities").EnumerateArray()
            .FirstOrDefault(candidate => candidate.GetProperty("entityId").GetString() == target);
        if (entity.ValueKind == JsonValueKind.Undefined)
        {
            return (1, 1);
        }

        var rect = entity.GetProperty("rect");
        return (
            rect.GetProperty("x").GetInt32() + (rect.GetProperty("width").GetInt32() / 2),
            rect.GetProperty("y").GetInt32() + (rect.GetProperty("height").GetInt32() / 2));
    }

    /// <summary>
    /// A model whose script is: act, act again on the same window, then try to leave the GUI. The point of
    /// the shape is the second act — a human approved the first, and the <see cref="TaskLeaseStore"/> is
    /// what decides whether the third parks again or runs. Nothing here presses anything: keystrokes at a
    /// background window are refusals the guards produce before any input is injected.
    /// <para>The third act is <c>desktop_exec</c>, which cannot ride the grant — approvals are forked by
    /// what leaves the GUI, never by what stays inside a window.</para>
    /// </summary>
    private sealed class LeaseThenActLlm : ILlmClient
    {
        private readonly List<LlmRequest> _requests = new();

        public IReadOnlyList<LlmRequest> Requests => _requests;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            _requests.Add(request);

            var toolResults = request.Messages.Count(message => message.Role == "tool");

            return Task.FromResult(toolResults switch
            {
                0 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c1", "desktop_observe", "{}") },
                    "tool_calls"),
                1 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c2", "desktop_act", $"{{ \"entityId\": \"{PickTargetOf(_requests)}\", \"kind\": \"KeyPress\", \"virtualKey\": 67 }}") },
                    "tool_calls"),
                // ■ The lease probe: same window, same kind. Approved once above, so this runs without
                // approval — that is exactly what the check reads back from the server state.
                2 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c3", "desktop_act", $"{{ \"entityId\": \"{PickTargetOf(_requests)}\", \"kind\": \"KeyPress\", \"virtualKey\": 67 }}") },
                    "tool_calls"),
                // What a grant must never cover: a command that leaves the GUI asks every time.
                3 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c4", "desktop_exec", "{ \"command\": \"echo hi\" }") },
                    "tool_calls"),
                _ => new LlmResponse("好吧。", Array.Empty<LlmToolCall>(), "stop"),
            });
        }

        public Task<LlmResponse> StreamAsync(LlmRequest request, Action<string> onText, CancellationToken ct = default) =>
            CompleteAsync(request, ct);
    }

    /// <summary>Waits until a host answers, so a probe is not a guess at a duration.</summary>
    private static async Task WaitForHostAsync(HttpClient http)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = await http.GetAsync("/api/health");
                if (probe.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("the host never started listening");
    }

    /// <summary>
    /// Waits for a queue item to leave the view.
    /// <para>
    /// The view is served from what is still in play rather than from the log, so an item settles by
    /// disappearing from it — there is no <c>Done</c> row to poll for. What the turn produced is asserted from
    /// the session afterwards, which is where a settled item's record lives.
    /// </para>
    /// </summary>
    private static async Task<bool> WaitForQueueItemToSettleAsync(HttpClient http, string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var polled = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var listed = polled.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("itemId").GetString() == itemId);
            if (!listed)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
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
        Results.Add((what, ok, detail, Skipped: false));

        if (ok)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}{(detail is null ? "" : $"  → {detail}")}");
    }

    /// <summary>
    /// The desktop checks read the real desktop (pit 11/12): one extra window from a running Partner app
    /// adds seconds to every UIA walk and the timing-sensitive scenes degrade wholesale, reads looking like
    /// regressions that are not. Warned at the top, where the operator still decides to stop rather than
    /// after the desktop section spent a minute failing.
    /// </summary>
    private static void WarnIfPartnerAppIsRunning()
    {
        var suspects = System.Diagnostics.Process.GetProcessesByName("partner-api")
            .Concat(System.Diagnostics.Process.GetProcessesByName("partner-shell"))
            .Concat(System.Diagnostics.Process.GetProcessesByName("Partner"))
            .ToList();

        if (suspects.Count == 0)
        {
            return;
        }

        Console.WriteLine(
            $"  WARN  检测到疑似本应用进程在跑（{string.Join("、", suspects.Select(p => $"{p.ProcessName} (pid {p.Id})"))}）："
            + "桌面结果可能整片失真——建议先停应用再跑 smoke");
    }

    /// <summary>A capture that pre-reports one positioned frame for any window, so the render of an act
    /// answer is drift-free to test without engaging the user's screen.</summary>
    private sealed class PositionEchoCapture(PixelRect at) : IScreenCapture
    {
        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) => Task.FromResult(
            new CaptureResult
            {
                Ok = true,
                Width = at.Width,
                Height = at.Height,
                Bgra = new byte[at.Width * at.Height * 4],
                Method = "smoke.stub",
                ScreenRect = at,
            });

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }

    /// <summary>
    /// A check that did not run, and why — reported as neither pass nor fail.
    /// <para>
    /// This exists because "the capability regressed" and "this machine's desktop was busy" are different
    /// facts, and the gate used to hear them as the same one: a check that manipulates the reader's real
    /// windows fails for reasons that have nothing to do with the code (a fullscreen game takes the focus
    /// back the moment you minimize it), and a build that goes red on such a machine is a build people learn
    /// to ignore. A skipped check is not a passed one — the capability it belongs to reports as *not
    /// verified this round*, which is a state the manifest can say out loud.
    /// </para>
    /// </summary>
    private static void Skip(string what, string reason)
    {
        Results.Add((what, true, reason, Skipped: true));
        Console.WriteLine($"  SKIP  {what}  → {reason}");
    }

    /// <summary>
    /// Whether the live desktop checks may run: they move and restore the reader's own windows, so they are
    /// opt-in, exactly like <c>DesktopLiveInteractionTests</c> (which moves the pointer, so it is gated on the
    /// same variable). Everything that only reads the desktop still runs by default.
    /// </summary>
    private static bool LiveDesktop => Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") == "1";

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
            "memory: a hit carries the tier and retention of the record it matched",
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
            "memory: the recall hook injects through the budget and marks the cut",
        }),
        ("memory.maintenance_pass", "M0", new[]
        {
            "memory: the maintenance pass ages and trims an untouched store",
        }),
        ("memory.embeddings_switch", "M0", new[]
        {
            "memory: embeddings default off and the switch round-trips",
        }),
        ("settings.no_model_shrink_saves", "M0", new[]
        {
            "settings: shrinking the window without a model saves and reports the deferred compaction",
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
            "desktop: positional input is absolute in the 0..65535 virtual-desktop space",
            "desktop: a point outside the virtual desktop is refused instead of clamped",
        }),
        // Only the routing half is registered here, because it is the half the smoke can observe without
        // injecting anything. That a named button really is pressed through its pattern is proven by
        // DesktopLiveInteractionTests, which is opt-in: it moves the pointer and shows a window.
        ("desktop.uia_pattern_routing", "M9", new[]
        {
            "desktop: an action a pattern cannot express is routed to injected input",
            "desktop: a control id that does not resolve is not guessed at",
        }),
        // The draw itself (grid lines, chips, the scaled-picture offset) is covered by ScreenshotMarksTests and
        // by the opt-in live test that compares a mark against the application's own PointToScreen; what is
        // registered here is the part the model consumes, which the smoke can observe read-only.
        ("desktop.screenshot_marks", "M9", new[]
        {
            "desktop: a picture's marks carry each control's real screen point",
        }),
        ("desktop.minimized_window", "M9", new[]
        {
            "desktop: a minimized window is reported as minimized rather than as a small window",
            "desktop: a minimized window is refused as a picture instead of a 160×28 stub",
            "desktop: a click into a minimized window's placeholder rect is refused",
            "desktop: focusing a minimized window restores it instead of only lighting up the taskbar",
            "desktop: a restored window is screenshot from the stale observation instead of being refused",
        }),
        ("desktop.act_targeting", "M9", new[]
        {
            "desktop: a point with no window id resolves to the window that covers it",
            "desktop: a point no window covers is refused with the candidates named",
            "desktop: the window under a point is the top-level window that contains it",
        }),
        ("desktop.model_vision", "M9", new[]
        {
            "desktop: a screenshot reaches the model as an image",
            "desktop: the log records the screenshot by media id, not by pixels",
        }),
        // The act answer carries the state after the action. The decision and the recording are what the
        // smoke can drive read-only (the positive click itself is a live test's job, which moves the user's
        // pointer and is deliberately skipped here).
        ("desktop.act_returns_state", "M9", new[]
        {
            "desktop: an act answer promises a picture for the kinds that change what is seen",
            "desktop: an act answer's attached picture maps back and is recorded by id",
        }),
        ("desktop.step_budget_by_family", "M9", new[]
        {
            "desktop: the step budget is measured by tool family, not one number",
        }),
        ("desktop.task_lease_approval", "M9", new[]
        {
            "desktop: an approved action opens a continuing grant on its window",
            "desktop: a granted window does not re-ask for the same kind",
            "desktop: a grant never covers leaving the GUI even while one is live",
        }),
        ("desktop.wait_primitive", "M9", new[]
        {
            "desktop: a wait for a control that is already there answers at once with the new id",
        }),
        ("desktop.desktop_scope", "M9", new[]
        {
            "desktop: a full-desktop picture exists without any observation and is recorded as desktop",
            "desktop: px/py on the desktop picture resolves to the point it was taken from",
        }),
        ("desktop.px_snap_to_element", "M9", new[]
        {
            "desktop: a px/py miss within the snap radius resolves to the control it was for",
        }),
        ("desktop.telemetry_aggregates", "M9", new[]
        {
            "desktop: telemetry aggregates the turn the model just ran",
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
        ("voice.hosted_loop", "M10/M11", new[]
        {
            "voice: enabling the halves starts them on the configured devices",
            "voice: an utterance is answered by a turn and the answer is spoken",
            "voice: the utterance is consumed and the halves stay up",
            "voice: a device that will not open is reported without stopping the other half",
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
        ("agent.queue_consumer", "M3", new[]
        {
            "consumer: without a model it is disabled, says why, and leaves work queued",
            "consumer: with a model the delivered input becomes a turn in the session",
            "consumer: the queue drains and the loop counts what it handled",
            "consumer: a real HTTP model call completes a turn end to end",
            "session: the reply is visible while it is still being written",
            "session: the client asks the model to stream",
            "session: the finished reply replaces the live row, whole",
            "session: tool calls and results reach the view",
            "image: an upload comes back as an id",
            "image: the model receives the attachment inline",
            "image: the logs keep the id, not the bytes",
            "desktop: a key press waits for approval",
            "desktop: an approved action looks again instead of trusting a dead lease",
            "desktop: a screenshot reaches the model as an image",
            "desktop: the log records the screenshot by media id, not by pixels",
            "desktop: the default gear asks for every action but never for looking",
            "desktop: the middle gear frees the GUI and still gates commands",
            "desktop: the open gear asks for nothing",
            "desktop: the capability reports itself enabled",
            "desktop: the model can observe through its tool",
            "desktop: an action waits for approval instead of running",
            "desktop: the approval queue surfaces the parked action",
            "desktop: answering the queue settles it",
            "desktop: a denied action is refused, not performed",
            "desktop: the refusal is in the log with the tool's name",
            "settings: pointing the model settings at an endpoint re-points the consumer",
            "settings: the API key is sent to the model but never echoed back",
            "settings: the thinking gear reaches the model request",
            "input: a gear chosen for one message wins over the configured default",
            "input: a gear that is not on the menu is rejected and lists the ones that are",
            "settings: the model listing is pulled through the API",
            "settings: an unreachable model endpoint returns a reason, not a silent empty list",
            "settings: shrinking the context compacts the history once",
            "settings: the compaction leaves exactly one summary standing in for the history",
        }),
        ("session.removal", "M1", new[]
        {
            "session: deleting one event takes its text out of the log",
            "session: retracting removes the selected event and takes its text out of the log",
            "session: events after a retraction stay visible",
        }),
        ("ui.composer", "M13", new[]
        {
            "ui: submitting from the composer queues work without executing it",
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
            "ui: settings round-trip every section through the API",
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
            var byEvidence = new Dictionary<string, List<(string Name, bool Passed, string? Detail, bool Skipped)>>(StringComparer.Ordinal);
            foreach (var name in c.Checks)
            {
                byEvidence[name] = byName.TryGetValue(name, out var runs) ? runs : new List<(string, bool, string?, bool)>();
            }

            // Skipped is neither ran nor missing: the check was named, declared, and deliberately not
            // executed. Counting it as missing would downgrade the capability, and counting it as passed would
            // claim evidence that does not exist.
            var missing = c.Checks.Where(n => !byName.ContainsKey(n)).ToArray();
            var observed = byEvidence.Values.SelectMany(r => r).Where(o => !o.Skipped).ToList();
            var skipped = byEvidence.Values.SelectMany(r => r).Count(o => o.Skipped);
            var failed = observed.Count(o => !o.Passed);

            // A capability reaches product-supported only if every check it names actually ran. Two of the five that were
            // skipped leave it covered — not supported, and not silently promoted either.
            var level = observed.Count == 0
                ? CapabilityLevel.NotSupported
                : missing.Length == 0 && failed == 0 && skipped == 0
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
                    byEvidence[n].Count > 0 && byEvidence[n].Any(o => !o.Skipped),
                    byEvidence[n].Count == 0 || byEvidence[n].All(o => o.Passed),
                    byEvidence[n].Count > 0 && byEvidence[n].All(o => o.Skipped))).ToList(),
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
