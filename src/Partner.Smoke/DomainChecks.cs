global using System.Globalization;
global using System.Net.Http.Headers;
global using System.Net.Http.Json;
global using System.Text.Json;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Partner.Api;
global using Partner.Api.Auth;
global using Partner.Api.Endpoints;
global using Partner.Api.Settings;
global using Partner.Api.Voice;
global using Partner.Core.Agent;
global using Partner.Core.Agent.Governance;
global using Partner.Core.Agent.Hooks;
global using Partner.Core.Agent.Llm;
global using Partner.Core.Agent.Mcp;
global using Partner.Core.Agent.Skills;
global using Partner.Core.Agent.Tools;
global using Partner.Core.Contracts;
global using Partner.Core.Media;
global using Partner.Core.Memory;
global using Partner.Core.Queue;
global using Partner.Core.Scheduler;
global using Partner.Core.Sessions;
global using Partner.Desktop;
global using Partner.Desktop.Capture;
global using Partner.Desktop.Input;
global using Partner.Desktop.Interop;
global using Partner.Desktop.Perception;
global using Partner.Desktop.Tools;
global using Partner.Desktop.Windows;
global using Partner.Game.Adapter;
global using Partner.Game.Perception;
global using Partner.Game.Reflex;
global using Partner.Voice;
global using Partner.Voice.Asr;
global using Partner.Voice.Audio;
global using Partner.Voice.Perception;
global using Partner.Voice.Playback;
global using Partner.Voice.Settings;
global using Partner.Voice.Tts;
global using System.Net.Sockets;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using Partner.Core.Manifest;

namespace Partner.Smoke;

internal static partial class Program
{
    private static async Task SessionQueueChecksAsync(string root, TimeProvider clock)
    {

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
                    // Two hooks inject: the smoke's own hook, then the skills index.
                    SessionEventKind.System,
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
            // Backoff is disabled so the second pass exercises recovery rather than the retry timer; the delay
            // itself is covered by the scheduler tests.
            await using (var failing = new JsonlScheduler(
                root,
                new FailOnceQueue(queue5),
                clock,
                new SchedulerOptions { RetryBaseDelay = TimeSpan.Zero }))
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

    }
}
