using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Agent.Mcp;
using Partner.Desktop.Tools;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// MCP wiring: the tool table, the governance boundary, and the settings round-trip. The protocol client is
/// covered by <c>StdioMcpClientTests</c> in Core; these tests are about the host calling it.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    /// <summary>A throwaway workspace, disposed with the context that owns it.</summary>
    private static string Workspace(string name) =>
        Path.Combine(Path.GetTempPath(), "partner-mcp-tests", name, Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_tool_name_is_qualified_by_its_server()
    {
        // Two servers may both publish a tool called "read". Without the prefix, whichever registers last
        // silently replaces the other and the model never learns there was a choice.
        var name = NamespacedMcpTool.Name("files", "read");

        Assert.Equal("mcp_files_read", name);
        Assert.True(NamespacedMcpTool.TryUnprefix(name, out var server, out var tool));
        Assert.Equal("files", server);
        Assert.Equal("read", tool);
    }

    [Fact]
    public void A_server_name_containing_an_underscore_still_round_trips()
    {
        // The split is at the first underscore after the prefix, so the tool name is whatever follows. The
        // registry resolves the server half; a name that does not match a running server is simply not a match.
        var name = NamespacedMcpTool.Name("my_files", "read");

        Assert.True(NamespacedMcpTool.TryUnprefix(name, out var server, out var tool));
        Assert.Equal("my", server);
        Assert.Equal("files_read", tool);
    }

    [Theory]
    [InlineData("desktop_act")]
    [InlineData("mcp_")]
    [InlineData("mcp__read")]
    [InlineData("mcp_files_")]
    [InlineData("")]
    public void A_name_that_is_not_a_qualified_mcp_tool_is_not_treated_as_one(string name)
    {
        // Governance routes on this, so a false positive would hand a desktop tool to the MCP branch.
        Assert.False(NamespacedMcpTool.TryUnprefix(name, out _, out _));
    }

    [Fact]
    public async Task An_mcp_tool_is_asked_for_rather_than_denied()
    {
        // Fail-closed, but an MCP tool must not be denied outright. The capability has to be on for the tool
        // to be reachable; the off case is its own test below.
        await using var context = await ApiContext.OpenAsync(Workspace("mcp-ask"));
        context.Settings.Save(context.Settings.Load() with { Mcp = new McpSettings { Enabled = true } });

        var decision = new DesktopGovernance(context).Decide("mcp_files_read", "{}");

        Assert.Equal(ToolDecision.Ask, decision.Decision);
    }

    [Fact]
    public async Task An_mcp_tool_on_the_allow_list_may_run_without_asking()
    {

        var context = await ApiContext.OpenAsync(Workspace("mcp"));
        context.Settings.Save(context.Settings.Load() with
        {
            Mcp = new McpSettings { Enabled = true, AllowedTools = new[] { "mcp_files_read" } },
        });

        Assert.Equal(ToolDecision.Allow, new DesktopGovernance(context).Decide("mcp_files_read", "{}").Decision);
    }

    [Fact]
    public async Task An_mcp_tool_is_denied_when_the_capability_is_off()
    {
        // Switching the server off must actually stop it, not merely stop offering it.

        var context = await ApiContext.OpenAsync(Workspace("mcp"));

        var decision = new DesktopGovernance(context).Decide("mcp_files_read", "{}");

        Assert.Equal(ToolDecision.Deny, decision.Decision);
        Assert.Contains("MCP", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mcp_section_survives_a_settings_round_trip()
    {
        // The UI refills its form from the response, so a section missing from the response flips the switch
        // back to off the moment it is saved.
        var settings = new RuntimeSettings
        {
            Mcp = new McpSettings
            {
                Enabled = true,
                Servers = new[] { new McpServerSettings { Name = "files", Command = "node", Arguments = new[] { "s.js" } } },
                AllowedTools = new[] { "mcp_files_read" },
            },
        };

        var view = SettingsView.From(settings);

        Assert.True(view.Mcp.Enabled);
        Assert.Equal("files", view.Mcp.Servers[0].Name);
        Assert.Equal(new[] { "mcp_files_read" }, view.Mcp.AllowedTools);
    }

    [Fact]
    public void Two_default_mcp_sections_compare_equal()
    {
        // Records holding a list compare it by reference, so two defaults would look different and every
        // settings save would restart the consumer — which here means killing child processes.
        Assert.True(new McpSettings().Equals(new McpSettings()));
        Assert.True(new McpServerSettings { Name = "a", Command = "x" }
            .Equals(new McpServerSettings { Name = "a", Command = "x" }));
        Assert.False(new McpServerSettings { Name = "a", Command = "x", Arguments = new[] { "1" } }
            .Equals(new McpServerSettings { Name = "a", Command = "x" }));
    }

    [Fact]
    public async Task A_desktop_tool_is_still_decided_by_the_desktop_gear()
    {
        // The MCP branch must not swallow the desktop tools: reads stay ungated, actions ask.
        await using var context = await ApiContext.OpenAsync(Workspace("mcp-desktop"));
        context.Settings.Save(context.Settings.Load() with
        {
            Desktop = new DesktopSettings { Enabled = true, ExecEnabled = true, Approval = ApprovalGears.All },
            Mcp = new McpSettings { Enabled = true },
        });

        var governance = new DesktopGovernance(context);

        // desktop_observe only looks, so it is allowed; desktop_exec acts, so it asks.
        Assert.Equal(ToolDecision.Allow, governance.Decide("desktop_observe", "{}").Decision);
        Assert.Equal(ToolDecision.Ask, governance.Decide("desktop_exec", "{}").Decision);
        Assert.Equal(ToolDecision.Ask, governance.Decide("mcp_files_read", "{}").Decision);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.None } });
        Assert.Equal(ToolDecision.Deny, governance.Decide("desktop_exec", "{}").Decision);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, ExecEnabled = true, Approval = ApprovalGears.None } });
        Assert.Equal(ToolDecision.Ask, governance.Decide("desktop_exec", "{}").Decision);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = false, ExecEnabled = true } });
        Assert.Equal(ToolDecision.Deny, governance.Decide("desktop_exec", "{}").Decision);
    }

    [Fact]
    public async Task Desktop_command_rechecks_permission_after_approval()
    {
        await using var context = await ApiContext.OpenAsync(Workspace("exec-revoked"));
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, ExecEnabled = true } });
        var tool = new Partner.Api.Agents.EnabledDesktopExecTool(context);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true } });
        var result = await tool.InvokeAsync("""{"command":"echo should-not-run"}""");
        Assert.False(result.Ok);
        Assert.Contains("关闭", result.Content);
    }
}

public sealed class DesktopApprovalLeaseTests
{
    private const string Arguments = """{"entityId":"ff","kind":"LeftClick"}""";

    private sealed class DelayedApproval : IApprovalHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            Started.TrySetResult();
            if (Interlocked.Increment(ref _count) == 2) BothStarted.TrySetResult();
            return Answer.Task;
        }
    }

    private sealed class DesktopAction : ITool
    {
        public ToolSpec Spec { get; } = new("desktop_act", "test action", "{}");
        public int Calls;
        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolResult(true, "done"));
        }
    }

    [Theory]
    [InlineData(0)] // Revoked while approval is pending.
    [InlineData(1)] // Approval timed out.
    [InlineData(2)] // Turn canceled.
    public async Task Rejected_or_late_approval_never_grants_a_lease(int scenario)
    {
        var dir = Path.Combine(Path.GetTempPath(), "partner-lease-tests", Guid.NewGuid().ToString("N"));
        await using var context = await ApiContext.OpenAsync(dir);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All } });
        var approval = new DelayedApproval();
        var action = new DesktopAction();
        var tools = new InMemoryToolRegistry();
        tools.Register(action);
        var (store, _) = JsonlSessionStore.Open(Path.Combine(dir, "session"));
        await using (store)
        {
            var runner = new AgentTurnRunner(store,
                new ScriptedLlmClient([
                    new LlmResponse(null, [new LlmToolCall("c1", "desktop_act", Arguments)], "tool_calls"),
                    new LlmResponse("done", [], "stop")]),
                tools, new DesktopGovernance(context), new LeaseGrantingApprovalHandler(approval, context.TaskLeases),
                new HookRegistry(), options: new AgentTurnOptions { ApprovalTimeout = TimeSpan.FromMilliseconds(scenario == 1 ? 50 : 5000) });
            using var cancellation = new CancellationTokenSource();
            var turn = runner.RunAsync(new InputItem
            {
                ItemId = "item-1", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
                Payload = "go", IdempotencyKey = "text:client:1",
            }, cancellation.Token);
            await approval.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (scenario == 1)
                await turn.WaitAsync(TimeSpan.FromSeconds(5));
            else if (scenario == 2)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
                context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = false, Approval = ApprovalGears.All } });
            approval.Answer.TrySetResult(true);
            if (scenario != 2)
                await turn.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, action.Calls);
            Assert.Empty(context.TaskLeases.Snapshot());
            context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All } });
            Assert.Equal(ToolDecision.Ask, new DesktopGovernance(context).Decide("desktop_act", Arguments).Decision);
        }
    }

    [Fact]
    public async Task Concurrent_valid_approvals_both_run_and_leave_a_usable_lease()
    {
        var dir = Path.Combine(Path.GetTempPath(), "partner-lease-tests", Guid.NewGuid().ToString("N"));
        await using var context = await ApiContext.OpenAsync(dir);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All } });
        var approval = new DelayedApproval();
        var action = new DesktopAction();
        var tools = new InMemoryToolRegistry();
        tools.Register(action);
        var (store, _) = JsonlSessionStore.Open(Path.Combine(dir, "session"));
        await using (store)
        {
            var runner = new AgentTurnRunner(store,
                new ScriptedLlmClient([
                    new LlmResponse(null, [new LlmToolCall("c1", "desktop_act", Arguments), new LlmToolCall("c2", "desktop_act", Arguments)], "tool_calls"),
                    new LlmResponse("done", [], "stop")]),
                tools, new DesktopGovernance(context), new LeaseGrantingApprovalHandler(approval, context.TaskLeases), new HookRegistry());
            var turn = runner.RunAsync(new InputItem
            {
                ItemId = "item-1", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
                Payload = "go", IdempotencyKey = "text:client:1",
            });
            await approval.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            approval.Answer.TrySetResult(true);
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, action.Calls);
            Assert.Single(context.TaskLeases.Snapshot());
            Assert.Equal(ToolDecision.Allow, new DesktopGovernance(context).Decide("desktop_act", Arguments).Decision);
        }
    }

    [Fact]
    public async Task Concurrent_grants_survive_stale_approval_and_expire_when_authorization_changes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "partner-lease-tests", Guid.NewGuid().ToString("N"));
        await using var context = await ApiContext.OpenAsync(dir);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All } });
        var governance = new DesktopGovernance(context);
        var stale = governance.Decide("desktop_act", Arguments);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All } });
        var valid = governance.Decide("desktop_act", Arguments);
        var handler = new LeaseGrantingApprovalHandler(new DenyAllApprovalHandler(), context.TaskLeases);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => handler.Accept("desktop_act", Arguments, valid.IsStillAuthorized))));
        handler.Accept("desktop_act", Arguments, stale.IsStillAuthorized);
        Assert.Single(context.TaskLeases.Snapshot());
        Assert.Equal(ToolDecision.Allow, governance.Decide("desktop_act", Arguments).Decision);
        context.Settings.Save(context.Settings.Load() with { Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All } });
        Assert.Empty(context.TaskLeases.Snapshot());
        Assert.Equal(ToolDecision.Ask, governance.Decide("desktop_act", Arguments).Decision);
    }
}

public sealed class McpAssemblyTests : ApiFixture
{
    protected override string WorkspaceName => "partner-mcp-assembly-tests";

    private void Configure(params string[] names)
    {
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Mcp = new McpSettings
            {
                Enabled = true,
                Servers = names.Select(name => new McpServerSettings { Name = name, Command = "unused" }).ToArray(),
            },
        });
    }

    [Fact]
    public async Task Connections_are_parallel_and_results_follow_settings_order_despite_failures()
    {
        Configure("slow", "badstart", "badlist", "fast");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new[] { new FakeMcp("slow", gate.Task), new FakeMcp("badstart", gate.Task) { FailStart = true },
            new FakeMcp("badlist", gate.Task) { FailList = true }, new FakeMcp("fast", gate.Task) };
        _consumer.McpClientFactory = config => clients.Single(client => client.ServerName == config.Name);

        var starting = _consumer.ReconfigureAsync(null, "test", new ScriptedLlmClient([]));
        await Task.WhenAll(clients.Select(client => client.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(starting.IsCompleted);
        gate.SetResult();
        var state = await starting;

        Assert.Equal(new[] { "mcp_slow_tool", "mcp_fast_tool" }, state.Tools.Select(tool => tool.Name).Where(name => name.StartsWith("mcp_")));
        Assert.Contains("badstart: start failed", _consumer.McpNote);
        Assert.Contains("badlist: tools/list 失败（list failed）", _consumer.McpNote);
        Assert.True(_consumer.McpNote!.IndexOf("badstart", StringComparison.Ordinal) < _consumer.McpNote.IndexOf("badlist", StringComparison.Ordinal));
        Assert.True(clients[1].Disposed);
        await _consumer.ReconfigureAsync(null, "stopped");
        Assert.All(clients, client => Assert.True(client.Disposed));
    }

    [Fact]
    public async Task Mcp_tools_appear_only_in_main_status_and_keep_independent_approval()
    {
        Configure("one", "two");
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Desktop = settings.Desktop with { Enabled = true },
            Mcp = settings.Mcp with { AllowedTools = ["mcp_one_tool"] },
        });
        _consumer.McpClientFactory = config => new FakeMcp(config.Name, Task.CompletedTask);

        var state = await _consumer.ReconfigureAsync(null, "test", new ScriptedLlmClient([]));

        Assert.Contains(state.Tools, tool => tool.Name == "mcp_one_tool" && tool.Decision == "allow");
        Assert.Contains(state.Tools, tool => tool.Name == "mcp_two_tool" && tool.Decision == "ask");
        Assert.NotEmpty(state.AgentTools);
        Assert.DoesNotContain(state.AgentTools.SelectMany(agent => agent.Tools),
            tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rebuilding_the_consumer_reuses_mcp_connections_and_tools_until_server_configuration_changes()
    {
        Configure("files");
        var clients = new List<FakeMcp>();
        _consumer.McpClientFactory = config =>
        {
            var client = new FakeMcp(config.Name, Task.CompletedTask);
            clients.Add(client);
            return client;
        };
        var llm = new ScriptedLlmClient([]);
        var first = await _consumer.ReconfigureAsync(null, "first", llm);
        Assert.Contains(first.Tools, tool => tool.Name == "mcp_files_tool");

        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with { Mcp = settings.Mcp with { AllowedTools = ["mcp_files_tool"] } });
        var rebuilt = await _consumer.ReconfigureAsync(null, "model changed", llm);
        Assert.Single(clients);
        Assert.True(clients[0].IsRunning);
        Assert.Contains(rebuilt.Tools, tool => tool.Name == "mcp_files_tool");
        Assert.Equal(ToolDecision.Allow, new DesktopGovernance(_context).Decide("mcp_files_tool", "{}").Decision);

        settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Mcp = settings.Mcp with { Servers = [settings.Mcp.Servers[0] with { Arguments = ["changed"] }] },
        });
        var changed = await _consumer.ReconfigureAsync(null, "server changed", llm);
        Assert.Equal(2, clients.Count);
        Assert.True(clients[0].Disposed);
        Assert.True(clients[1].IsRunning);
        Assert.Contains(changed.Tools, tool => tool.Name == "mcp_files_tool");

        settings = _context.Settings.Load();
        _context.Settings.Save(settings with { Mcp = settings.Mcp with { Enabled = false } });
        var disabled = await _consumer.ReconfigureAsync(null, "disabled", llm);
        Assert.True(clients[1].Disposed);
        Assert.DoesNotContain(disabled.Tools, tool => tool.Name.StartsWith("mcp_"));
    }

    [Fact]
    public async Task Refreshing_capability_and_image_exposure_keeps_mcp_connection_and_injected_model()
    {
        Configure("files");
        var clients = new List<FakeMcp>();
        _consumer.McpClientFactory = config =>
        {
            var client = new FakeMcp(config.Name, Task.CompletedTask);
            clients.Add(client);
            return client;
        };
        await _consumer.ReconfigureAsync(null, "test", new ScriptedLlmClient([]));
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Web = settings.Web with { Enabled = !settings.Web.Enabled },
            Model = settings.Model with { SupportsImages = !settings.Model.SupportsImages },
        });
        await _consumer.RefreshToolsAsync();

        Assert.Single(clients);
        Assert.True(clients[0].IsRunning);
        Assert.False(clients[0].Disposed);
        Assert.True(_consumer.State.Running);
        Assert.Contains(_consumer.State.Tools, tool => tool.Name == "mcp_files_tool");
        Assert.Equal(!settings.Web.Enabled, _consumer.State.Tools.Any(tool => tool.Name == "web_search"));
    }

    [Fact]
    public async Task Ambiguous_qualified_names_are_not_registered_even_when_allowlisted()
    {
        Configure("a", "a_b", "safe");
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with { Mcp = settings.Mcp with { AllowedTools = ["mcp_a_b_tool"] } });
        _consumer.McpClientFactory = config => new FakeMcp(config.Name, Task.CompletedTask)
        {
            ToolName = config.Name == "a" ? "b_tool" : "tool",
        };

        var first = await _consumer.ReconfigureAsync(null, "test", new ScriptedLlmClient([]));
        Assert.DoesNotContain(first.Tools, tool => tool.Name == "mcp_a_b_tool");
        Assert.Contains(first.Tools, tool => tool.Name == "mcp_safe_tool");
        Assert.Contains("mcp_a_b_tool", _consumer.McpNote);

        await _consumer.ReconfigureAsync(null, "rebuild", new ScriptedLlmClient([]));
        Assert.Equal("MCP 工具名称冲突，已拒绝：mcp_a_b_tool", _consumer.McpNote);
    }

    [Fact]
    public async Task Cancellation_reclaims_both_completed_and_inflight_connections()
    {
        Configure("ready", "waiting");
        var ready = new FakeMcp("ready", Task.CompletedTask);
        var waiting = new FakeMcp("waiting", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        _consumer.McpClientFactory = config => config.Name == "ready" ? ready : waiting;
        using var cancel = new CancellationTokenSource();
        var starting = _consumer.ReconfigureAsync(null, "test", new ScriptedLlmClient([]), cancel.Token);
        await Task.WhenAll(ready.Listed.Task, waiting.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.True(ready.Disposed);
        Assert.True(waiting.Disposed);
        Assert.False(_consumer.State.Running);
    }

    [Fact]
    public async Task Reconfigure_to_stopped_cancels_inflight_assembly()
    {
        Configure("waiting");
        var waiting = new FakeMcp("waiting", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        _consumer.McpClientFactory = _ => waiting;
        var starting = _consumer.ReconfigureAsync(null, "test", new ScriptedLlmClient([]));
        await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = _consumer.ReconfigureAsync(null, "stopped");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.False((await stopping.WaitAsync(TimeSpan.FromSeconds(5))).Running);
        Assert.True(waiting.Disposed);
    }

    [Fact]
    public async Task Disposing_host_cancels_inflight_assembly()
    {
        Configure("waiting");
        var waiting = new FakeMcp("waiting", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var host = ConsumerHost.Create(_context, _workspace);
        host.McpClientFactory = _ => waiting;
        var starting = host.ReconfigureAsync(null, "test", new ScriptedLlmClient([]));
        await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = host.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(waiting.Disposed);
    }

    private sealed class FakeMcp(string name, Task gate) : IMcpClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Listed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailStart { get; init; }
        public bool FailList { get; init; }
        public string ToolName { get; init; } = "tool";
        public bool Disposed { get; private set; }
        public bool IsRunning { get; private set; }
        public string ServerName => name;

        public async Task StartAsync(CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await gate.WaitAsync(ct);
            if (FailStart) throw new InvalidOperationException("start failed");
            IsRunning = true;
        }

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default)
        {
            Listed.TrySetResult();
            if (FailList) throw new InvalidOperationException("list failed");
            IReadOnlyList<McpToolDescriptor> tools = [new(ToolName, "test", "{\"type\":\"object\"}")];
            return Task.FromResult(tools);
        }

        public Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) { IsRunning = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; IsRunning = false; return ValueTask.CompletedTask; }
    }
}
