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
