using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

/// <summary>A tool with a time budget of its own is not cut off at the runner's default.</summary>
public sealed class ToolTimeoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class SlowTool(string name, TimeSpan duration, TimeSpan? timeout) : ITimedTool
    {
        public ToolSpec Spec { get; } = new(name, "slow", "{\"type\":\"object\"}");

        public TimeSpan Timeout => timeout ?? TimeSpan.Zero;

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
            return new ToolResult(true, "finished");
        }
    }

    private sealed class BlockingTool : ITool
    {
        public ToolSpec Spec { get; } = new("blocking", "blocks until released", "{\"type\":\"object\"}");
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Release.Task.ConfigureAwait(false); // Deliberately ignores cancellation.
            return new ToolResult(true, "late result");
        }
    }

    private sealed class LateApproval(bool throwsOnCancellation = false) : IApprovalHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            try
            {
                using var registration = ct.Register(() => Canceled.TrySetResult(true));
                Started.TrySetResult(true);
                await Release.Task.ConfigureAwait(false);
                if (throwsOnCancellation) ct.ThrowIfCancellationRequested();
                return true;
            }
            finally
            {
                Finished.TrySetResult(true);
            }
        }
    }

    private sealed class CountingTool : ITool
    {
        public ToolSpec Spec { get; } = new("side_effect", "side effect", "{}");
        public int Calls;
        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolResult(true, "done"));
        }
    }

    private sealed class IndependentlyCanceledTool : ITool
    {
        public ToolSpec Spec { get; } = new("independent_cancel", "independent cancellation", "{}");

        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
            throw new OperationCanceledException("independent cancellation");
    }

    private sealed class ResultOnCancellationTool : ITool
    {
        public ToolSpec Spec { get; } = new("result_on_cancel", "completes on cancellation", "{}");

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => canceled.TrySetResult());
            await canceled.Task.ConfigureAwait(false);
            return new ToolResult(true, "actual result");
        }
    }

    private sealed class RevocableGovernance : IToolGovernance
    {
        public bool Authorized = true;
        public GovernanceDecision Decide(string toolName, string argumentsJson) =>
            new(ToolDecision.Ask, "approval required") { IsStillAuthorized = () => Volatile.Read(ref Authorized) };
    }

    [Fact]
    public async Task Revocation_during_approval_prevents_tool_dispatch()
    {
        var tool = new CountingTool();
        var approval = new LateApproval();
        var governance = new RevocableGovernance();
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(tool);
            var runner = new AgentTurnRunner(store,
                new ScriptedLlmClient([
                    new LlmResponse(null, [new LlmToolCall("c1", tool.Spec.Name, "{}")], "tool_calls"),
                    new LlmResponse("done", [], "stop")]),
                tools, governance, approval, new HookRegistry());
            var turn = runner.RunAsync(new InputItem
            {
                ItemId = "item-1", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
                Payload = "go", IdempotencyKey = "text:client:1",
            });
            await approval.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref governance.Authorized, false);
            approval.Release.TrySetResult(true);
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, tool.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_approval_after_timeout_never_starts_the_tool(bool throwsOnCancellation)
    {
        var tool = new CountingTool();
        var approval = new LateApproval(throwsOnCancellation);
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(tool);
            var runner = new AgentTurnRunner(store,
                new ScriptedLlmClient([
                    new LlmResponse(null, [new LlmToolCall("c1", tool.Spec.Name, "{}")], "tool_calls"),
                    new LlmResponse("done", [], "stop")]),
                tools, new AllowListGovernance([], [tool.Spec.Name]), approval, new HookRegistry(),
                options: new AgentTurnOptions { ApprovalTimeout = TimeSpan.FromMilliseconds(50), ToolDispatchTimeout = TimeSpan.FromSeconds(5) });
            try
            {
                var turn = runner.RunAsync(new InputItem
                {
                    ItemId = "item-1", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
                    Payload = "go", IdempotencyKey = "text:client:1",
                });
                await approval.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await approval.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await turn.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(approval.Release.Task.IsCompleted);
                var results = new List<string>();
                await foreach (var entry in store.ReadAllAsync())
                    if (entry.Kind == SessionEventKind.ToolResult) results.Add(entry.Body);
                Assert.Contains("approval canceled or timed out", Assert.Single(results));
                approval.Release.TrySetResult(true);
                await approval.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, tool.Calls);
            }
            finally
            {
                approval.Release.TrySetResult(true);
            }
        }
    }

    private async Task<string> RunAsync(ITool tool)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(tool);

            var runner = new AgentTurnRunner(
                store,
                new ScriptedLlmClient(new[]
                {
                    new LlmResponse(null, new[] { new LlmToolCall("c1", tool.Spec.Name, "{}") }, "tool_calls"),
                    new LlmResponse("done", Array.Empty<LlmToolCall>(), "stop"),
                }),
                tools,
                new AllowListGovernance(new[] { tool.Spec.Name }),
                new DenyAllApprovalHandler(),
                new HookRegistry(),
                options: new AgentTurnOptions { ToolTimeout = TimeSpan.FromMilliseconds(150) });

            await runner.RunAsync(new InputItem
            {
                ItemId = "item-1",
                Seq = 1,
                Status = InputItemStatus.Running,
                Source = InputSource.Text,
                Payload = "go",
                IdempotencyKey = "text:client:1",
            });

            await foreach (var e in store.ReadAllAsync())
            {
                if (e.Kind == SessionEventKind.ToolResult)
                {
                    return e.Body;
                }
            }

            return string.Empty;
        }
    }

    [Fact]
    public async Task Independent_tool_cancellation_is_not_reported_as_a_timeout()
    {
        var result = await RunAsync(new IndependentlyCanceledTool());
        Assert.Equal("error: independent cancellation", result);
    }

    [Fact]
    public async Task Tool_result_returned_after_its_timeout_is_not_replaced_by_a_timeout()
    {
        var result = await RunAsync(new ResultOnCancellationTool());
        Assert.Equal("actual result", result);
    }

    [Fact]
    public async Task A_tool_with_a_longer_budget_of_its_own_is_allowed_to_finish()
    {
        var body = await RunAsync(new SlowTool("slow_search", TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5)));

        Assert.Equal("finished", body);
    }

    [Fact]
    public async Task A_tool_without_one_is_still_cut_at_the_default()
    {
        var body = await RunAsync(new SlowTool("slow_other", TimeSpan.FromSeconds(5), timeout: null));

        Assert.StartsWith("timeout after", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shorter_budget_never_shortens_the_default()
    {
        // 100 ms of work, a declared budget of 50 ms, a default of 150 ms: the declaration cannot make it worse.
        var body = await RunAsync(new SlowTool("slow_short", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50)));

        Assert.Equal("finished", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gate_is_disposed_only_after_all_dispatch_tasks_finish(bool fault)
    {
        var gate = new SemaphoreSlim(1, 1);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task WorkAsync()
        {
            await gate.WaitAsync();
            try
            {
                started.SetResult(true);
                await release.Task;
                if (fault)
                {
                    throw new InvalidOperationException("late failure");
                }
            }
            finally
            {
                gate.Release();
            }
        }

        var work = WorkAsync();
        await started.Task;
        var cleanup = AgentTurnRunner.DisposeGateWhenCompleteAsync(Task.WhenAll(work), gate);
        Assert.False(cleanup.IsCompleted);
        release.SetResult(true);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fault, work.IsFaulted);
        Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
    }

    [Fact]
    public async Task Dispatch_timeout_returns_while_a_cancellation_ignoring_tool_is_still_running()
    {
        var tool = new BlockingTool();
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(tool);
            var runner = new AgentTurnRunner(
                store,
                new ScriptedLlmClient(new[]
                {
                    new LlmResponse(null, new[] { new LlmToolCall("c1", tool.Spec.Name, "{}") }, "tool_calls"),
                    new LlmResponse("done", Array.Empty<LlmToolCall>(), "stop"),
                }),
                tools,
                new AllowListGovernance(new[] { tool.Spec.Name }),
                new DenyAllApprovalHandler(),
                new HookRegistry(),
                options: new AgentTurnOptions { ToolDispatchTimeout = TimeSpan.FromMilliseconds(500) });

            try
            {
                var turn = runner.RunAsync(new InputItem
                {
                    ItemId = "item-1",
                    Seq = 1,
                    Status = InputItemStatus.Running,
                    Source = InputSource.Text,
                    Payload = "go",
                    IdempotencyKey = "text:client:1",
                });
                await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await turn.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(tool.Release.Task.IsCompleted);

                var results = new List<string>();
                await foreach (var e in store.ReadAllAsync())
                {
                    if (e.Kind == SessionEventKind.ToolResult)
                    {
                        results.Add(e.Body);
                    }
                }

                Assert.Single(results);
                Assert.StartsWith("timeout after", results[0], StringComparison.Ordinal);
                Assert.Contains("status unconfirmed", results[0]);
                Assert.Contains("verify before retrying", results[0]);
                Assert.EndsWith(tool.Spec.Name, results[0], StringComparison.Ordinal);
            }
            finally
            {
                tool.Release.TrySetResult(true);
            }
        }
    }

    [Fact]
    public async Task Caller_cancellation_records_completed_and_running_calls_in_model_order()
    {
        var completed = new CountingTool();
        var running = new BlockingTool();
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(completed);
            tools.Register(running);
            var runner = new AgentTurnRunner(store,
                new ScriptedLlmClient([
                    new LlmResponse(null, [
                        new LlmToolCall("c1", completed.Spec.Name, "{}"),
                        new LlmToolCall("c2", running.Spec.Name, "{}")], "tool_calls")]),
                tools, new AllowListGovernance([completed.Spec.Name, running.Spec.Name]),
                new DenyAllApprovalHandler(), new HookRegistry(),
                options: new AgentTurnOptions { MaxParallelToolCalls = 2, ToolDispatchTimeout = TimeSpan.FromMinutes(1) });
            using var cancellation = new CancellationTokenSource();
            try
            {
                var turn = runner.RunAsync(new InputItem
                {
                    ItemId = "item-1", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
                    Payload = "go", IdempotencyKey = "text:client:1",
                }, cancellation.Token);
                await running.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completed.Calls) == 1, TimeSpan.FromSeconds(5)));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));

                var results = new List<SessionEvent>();
                var ends = new List<SessionEvent>();
                await foreach (var entry in store.ReadAllAsync())
                {
                    if (entry.Kind == SessionEventKind.ToolResult) results.Add(entry);
                    if (entry.Kind == SessionEventKind.TurnEnd) ends.Add(entry);
                }
                Assert.Equal(2, results.Count);
                Assert.Equal("done", results[0].Body);
                Assert.Contains("status unconfirmed", results[1].Body);
                Assert.Equal("c1", results[0].Meta!["id"]);
                Assert.Equal("c2", results[1].Meta!["id"]);
                Assert.Equal(TurnEndReason.Aborted, Assert.Single(ends).Meta!["reason"]);
                Assert.Equal(1, completed.Calls);
            }
            finally
            {
                running.Release.TrySetResult(true);
            }
        }
    }

    [Fact]
    public async Task Caller_cancellation_during_uncooperative_approval_never_starts_tool()
    {
        var tool = new CountingTool();
        var approval = new LateApproval();
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(tool);
            var runner = new AgentTurnRunner(store,
                new ScriptedLlmClient([new LlmResponse(null, [
                    new LlmToolCall("c1", tool.Spec.Name, "{}"),
                    new LlmToolCall("c2", tool.Spec.Name, "{}")], "tool_calls")]),
                tools, new AllowListGovernance([], [tool.Spec.Name]), approval, new HookRegistry(),
                options: new AgentTurnOptions { MaxParallelToolCalls = 1, ToolDispatchTimeout = TimeSpan.FromMinutes(1) });
            using var cancellation = new CancellationTokenSource();
            try
            {
                var turn = runner.RunAsync(new InputItem
                {
                    ItemId = "item-1", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
                    Payload = "go", IdempotencyKey = "text:client:1",
                }, cancellation.Token);
                await approval.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));
                var results = new List<string>();
                await foreach (var entry in store.ReadAllAsync())
                    if (entry.Kind == SessionEventKind.ToolResult) results.Add(entry.Body);
                Assert.Equal(2, results.Count);
                Assert.All(results, result => Assert.Contains("tool was not started", result));
                approval.Release.TrySetResult(true);
                await approval.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, tool.Calls);
            }
            finally
            {
                approval.Release.TrySetResult(true);
            }
        }
    }

    [Fact]
    public void Latencies_are_summarised_per_name()
    {
        Partner.Core.Diagnostics.LatencyTracker.Reset();
        foreach (var ms in new[] { 100.0, 200, 300, 400, 1000 })
        {
            Partner.Core.Diagnostics.LatencyTracker.Record("probe", ms);
        }

        var probe = Assert.Single(Partner.Core.Diagnostics.LatencyTracker.Snapshot(), l => l.Name == "probe");
        Assert.Equal(5, probe.Count);
        Assert.Equal(300, probe.P50Ms);
        Assert.Equal(1000, probe.P95Ms);
        Assert.Equal(1000, probe.LastMs);
    }
}
