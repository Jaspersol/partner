using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Hosting;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

/// <summary>The budget that binds inside a turn: one big tool result, and a conversation that keeps growing.</summary>
public sealed class ToolResultBudgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
    private readonly ITokenCounter _counter = new ApproximateTokenCounter();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Records a snapshot of every request, because the runner mutates the list it sends.</summary>
    private sealed class SnapshotLlm(params LlmResponse[] scripted) : ILlmClient
    {
        private int _next;

        public List<List<LlmMessage>> Requests { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Requests.Add(request.Messages.ToList());
            return Task.FromResult(scripted[Math.Min(_next++, scripted.Length - 1)]);
        }
    }

    private static LlmResponse Call(string id) =>
        new(null, new[] { new LlmToolCall(id, "big", "{}") }, "tool_calls");

    private static LlmResponse Done() => new("done", Array.Empty<LlmToolCall>(), "stop");

    private static InputItem Claimed() => new()
    {
        ItemId = "item-1",
        Seq = 1,
        Status = InputItemStatus.Running,
        Source = InputSource.Text,
        Payload = "read it",
        IdempotencyKey = "text:client:1",
    };

    private AgentTurnRunner Runner(ISessionStore store, ILlmClient llm, AgentTurnOptions options, string toolBody)
    {
        var tools = new InMemoryToolRegistry();
        tools.Register(new FakeTool("big", (_, _) => Task.FromResult(new ToolResult(true, toolBody))));

        return new AgentTurnRunner(
            store,
            llm,
            tools,
            new AllowListGovernance(new[] { "big" }),
            new DenyAllApprovalHandler(),
            new HookRegistry(),
            options: options,
            tokens: _counter);
    }

    [Fact]
    public void An_oversized_result_keeps_its_head_and_tail_and_says_how_to_read_the_rest()
    {
        var body = "HEAD-" + new string('x', 200_000) + "-TAIL";

        var clamped = ToolResultBudget.Clamp(body, 1000, _counter);

        Assert.StartsWith("HEAD-", clamped);
        Assert.EndsWith("-TAIL", clamped);
        Assert.Contains("已截断", clamped);
        Assert.Contains("offset", clamped);
        Assert.True(_counter.Count(clamped) <= 1000 + 200, "the marker may add a little, nothing more");
    }

    [Fact]
    public void A_result_that_fits_is_returned_untouched()
    {
        Assert.Equal("short", ToolResultBudget.Clamp("short", 1000, _counter));
    }

    [Fact]
    public async Task A_huge_tool_result_never_reaches_the_next_model_call_whole()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var options = new AgentTurnOptions { ContextWindowTokens = 8192, MaxTokens = 1024 };
            var budget = ContextBudget.PromptBudget(options.ContextWindowTokens, options.MaxTokens);
            var llm = new SnapshotLlm(Call("c1"), Done());

            await Runner(store, llm, options, new string('x', 400_000)).RunAsync(Claimed());

            var second = llm.Requests[1];
            var tool = Assert.Single(second, m => m.Role == "tool");
            Assert.Contains("已截断", tool.Content);
            Assert.True(ToolResultBudget.Measure(second, _counter) <= budget);

            // The log carries the capped text too, so the next turn's replay cannot be blown by this entry.
            var logged = new List<SessionEvent>();
            await foreach (var e in store.ReadAllAsync())
            {
                logged.Add(e);
            }

            var result = Assert.Single(logged, e => e.Kind == SessionEventKind.ToolResult);
            Assert.Contains("已截断", result.Body);
        }
    }

    [Fact]
    public async Task Older_tool_results_are_folded_once_the_conversation_nears_the_window()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            // Each result is under the per-result cap but together they pass 80% of the budget.
            var options = new AgentTurnOptions { ContextWindowTokens = 8192, MaxTokens = 1024, MaxSteps = 8 };
            var budget = ContextBudget.PromptBudget(options.ContextWindowTokens, options.MaxTokens);
            var llm = new SnapshotLlm(Call("c1"), Call("c2"), Call("c3"), Call("c4"), Call("c5"), Done());

            await Runner(store, llm, options, new string('y', ToolResultBudget.ResultCap(budget) * 4 - 40))
                .RunAsync(Claimed());

            var last = llm.Requests[^1];
            var tools = last.Where(m => m.Role == "tool").ToList();

            Assert.Equal(5, tools.Count); // every call keeps its result: a provider rejects an orphan call
            Assert.Contains(tools, m => m.Content!.StartsWith("[较早的工具结果已折叠", StringComparison.Ordinal));
            Assert.DoesNotContain("较早", tools[^1].Content); // the newest result is still whole
            Assert.True(ToolResultBudget.Measure(last, _counter) <= budget);
        }
    }

    [Fact]
    public void Folding_never_touches_the_protected_latest_step()
    {
        var conversation = new List<LlmMessage>
        {
            new("tool", new string('a', 4000), ToolCallId: "1", Name: "big"),
            new("tool", new string('b', 4000), ToolCallId: "2", Name: "big"),
        };

        var folded = ToolResultBudget.CollapseOlder(conversation, protectFrom: 1, promptBudget: 1000, _counter);

        Assert.Equal(1, folded);
        Assert.StartsWith("[较早的工具结果已折叠", conversation[0].Content);
        Assert.Equal(new string('b', 4000), conversation[1].Content);
    }

    [Fact]
    public async Task The_after_turn_hook_runs_once_the_item_is_settled()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        await using (var queue = new JsonlInputQueue(_dir))
        {
            await queue.EnqueueAsync(new InputItem
            {
                ItemId = "item-1",
                Seq = 1,
                Source = InputSource.Text,
                Payload = "hi",
                IdempotencyKey = "text:item-1",
            });
            var claimed = await queue.TakeAsync();

            InputItemStatus? statusSeenByHook = null;
            var loop = new TurnLoop(
                queue,
                Runner(store, new SnapshotLlm(Done()), new AgentTurnOptions(), "x"),
                store,
                afterTurn: async _ =>
                {
                    await foreach (var entry in queue.ReadLogAsync())
                    {
                        if (entry.ItemId == "item-1")
                        {
                            statusSeenByHook = entry.Status;
                        }
                    }
                });

            await loop.ProcessAsync(claimed!);

            Assert.Equal(InputItemStatus.Done, statusSeenByHook);
        }
    }

    [Fact]
    public async Task The_settled_callback_hears_how_the_item_ended()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        await using (var queue = new JsonlInputQueue(_dir))
        {
            await queue.EnqueueAsync(new InputItem { ItemId = "ok", Seq = 1, Source = InputSource.Schedule, Payload = "hi", IdempotencyKey = "schedule:t1:x" });
            await queue.EnqueueAsync(new InputItem { ItemId = "bad", Seq = 2, Source = InputSource.Schedule, Payload = "hi", IdempotencyKey = "schedule:t2:x" });

            var settled = new List<(string Id, InputItemStatus Status, string? Error)>();
            var good = new TurnLoop(
                queue,
                Runner(store, new SnapshotLlm(Done()), new AgentTurnOptions(), "x"),
                store,
                onSettled: (item, status, error) =>
                {
                    settled.Add((item.ItemId, status, error));
                    return Task.CompletedTask;
                });
            await good.ProcessAsync((await queue.TakeAsync())!);

            var broken = new TurnLoop(
                queue,
                Runner(store, new ThrowingLlm(), new AgentTurnOptions(), "x"),
                store,
                onSettled: (item, status, error) =>
                {
                    settled.Add((item.ItemId, status, error));
                    return Task.CompletedTask;
                });
            await broken.ProcessAsync((await queue.TakeAsync())!);

            Assert.Equal(("ok", InputItemStatus.Done, (string?)null), settled[0]);
            Assert.Equal("bad", settled[1].Id);
            Assert.Equal(InputItemStatus.Failed, settled[1].Status);
            Assert.Contains("model exploded", settled[1].Error);
        }
    }

    private sealed class ThrowingLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("model exploded");
    }

    [Fact]
    public async Task A_failing_after_turn_hook_does_not_fail_the_item()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        await using (var queue = new JsonlInputQueue(_dir))
        {
            await queue.EnqueueAsync(new InputItem
            {
                ItemId = "item-1",
                Seq = 1,
                Source = InputSource.Text,
                Payload = "hi",
                IdempotencyKey = "text:item-1",
            });
            var claimed = await queue.TakeAsync();

            var loop = new TurnLoop(
                queue,
                Runner(store, new SnapshotLlm(Done()), new AgentTurnOptions(), "x"),
                store,
                afterTurn: _ => throw new InvalidOperationException("compaction exploded"));

            await loop.ProcessAsync(claimed!);

            Assert.Equal(new TurnLoopStats(1, 0, 0, 0), loop.Stats);
        }
    }
}
