using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Api.Agents;
using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// The desktop capability over HTTP. These checks stay desktop-independent on purpose: the parts that need a
/// real session (observing windows, the model's tool call) are covered by the smoke, which already assumes one.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    [Theory]
    [InlineData(AgentScenarios.Desktop, false, false)]
    [InlineData(AgentScenarios.Desktop, false, true)]
    [InlineData(AgentScenarios.Desktop, true, false)]
    [InlineData(AgentScenarios.Desktop, true, true)]
    [InlineData(AgentScenarios.FileRead, false, false)]
    [InlineData(AgentScenarios.WebSearch, false, false)]
    public void Scenario_registration_matches_catalogue(string scenario, bool images, bool exec)
    {
        var settings = _context.Settings.Load();
        settings = settings with { Desktop = settings.Desktop with { ExecEnabled = exec } };
        var tools = new InMemoryToolRegistry();

        AgentRouting.RegisterScenarioTools(scenario, tools, _context, settings, images, _workspace);

        Assert.Equal(AgentRouting.ScenarioTools(scenario, settings, images), tools.Specs.Select(spec => spec.Name));
    }

    [Fact]
    public void Unknown_scenario_does_not_mutate_the_registry()
    {
        var tools = new InMemoryToolRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentRouting.RegisterScenarioTools(
            "unknown", tools, _context, _context.Settings.Load(), false, _workspace));
        Assert.Empty(tools.Specs);
    }

    [Theory]
    [InlineData("none", ToolDecision.Allow)]
    [InlineData("dangerous", ToolDecision.Ask)]
    [InlineData("all", ToolDecision.Ask)]
    public void File_mutations_follow_approval_gear(string gear, ToolDecision expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Desktop = settings.Desktop with { Approval = gear },
            Files = new FilesSettings { Enabled = true, Roots = [_workspace], WriteEnabled = true, WriteRoots = [_workspace] },
        });
        var governance = new Partner.Api.DesktopGovernance(_context);
        Assert.Equal(expected, governance.Decide("file_write", "{}").Decision);
        Assert.Equal(expected, governance.Decide("file_edit", "{}").Decision);
    }

    /// <summary>
    /// The desktop sub-agent's tools with the decision governance will make for each, read off /api/consumer.
    /// The main agent holds none of them, so they are reported under <c>agentTools</c>.
    /// </summary>
    private async Task<List<(string Name, string Decision)>> DesktopToolsAsync()
    {
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        var desktop = state.GetProperty("agentTools").EnumerateArray()
            .FirstOrDefault(agent => agent.GetProperty("scenario").GetString() == "desktop");

        return desktop.ValueKind == JsonValueKind.Undefined
            ? []
            : desktop.GetProperty("tools").EnumerateArray()
                .Select(tool => (tool.GetProperty("name").GetString()!, tool.GetProperty("decision").GetString()!))
                .ToList();
    }

    [Fact]
    public async Task The_desktop_capability_is_off_until_it_is_switched_on()
    {
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/desktop");

        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(15000, state.GetProperty("leaseTtlMs").GetInt32());
        Assert.Null(state.GetProperty("lastObsId").GetString());
    }

    [Fact]
    public async Task Observing_is_refused_while_the_capability_is_off()
    {
        // Fail-closed: the default must not be "enumerate the user's windows because nobody said no".
        var response = await _http.PostAsync("/api/desktop/observe", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("未启用", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acting_is_refused_while_the_capability_is_off()
    {
        var response = await _http.PostAsJsonAsync("/api/desktop/act", new
        {
            entityId = "1a2b",
            kind = "LeftClick",
            x = 1,
            y = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Enabling_it_is_reported_back_and_survives_a_reload()
    {
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, leaseTtlMs = 4000, allowedWindowTitles = new[] { "记事本" } },
        });

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/desktop");
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(4000, state.GetProperty("leaseTtlMs").GetInt32());
        Assert.Contains(
            "记事本",
            state.GetProperty("allowedWindowTitles").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task The_settings_response_carries_the_section_the_ui_refills_from()
    {
        var saved = await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, leaseTtlMs = 7000, allowedWindowTitles = new[] { "记事本" } },
        });

        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var desktop = body.GetProperty("settings").GetProperty("desktop");
        Assert.True(desktop.GetProperty("enabled").GetBoolean());
        Assert.Equal(7000, desktop.GetProperty("leaseTtlMs").GetInt32());

        var read = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(read.GetProperty("desktop").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Toggling_it_changes_what_the_model_can_call()
    {
        // The tool table is built when the consumer starts, and /api/consumer reports it — so this observes the
        // restart that makes the switch mean something, rather than asserting that a request returned 200.
        var off = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        Assert.Empty(off.GetProperty("tools").EnumerateArray());
        Assert.Empty(await DesktopToolsAsync());

        await _http.PutSettingsAsync(new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = true, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var on = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        var mainTools = on.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToList();
        Assert.Contains("agent_delegate", mainTools);
        Assert.Contains("agent_job_status", mainTools);
        Assert.Contains("agent_job_cancel", mainTools);
        Assert.Contains("schedule_create", mainTools);
        Assert.Contains("schedule_list", mainTools);
        Assert.Contains("schedule_delete", mainTools);
        var governance = new Partner.Api.DesktopGovernance(_context);
        foreach (var name in mainTools)
            Assert.NotEqual(Partner.Core.Agent.Tools.ToolDecision.Deny, governance.Summarize(name).Decision);
        Assert.DoesNotContain(mainTools, name => name.StartsWith("desktop_", StringComparison.Ordinal));

        var tools = (await DesktopToolsAsync()).Select(tool => tool.Name + ":" + tool.Decision).ToList();
        Assert.Contains("desktop_observe:allow", tools);
        Assert.Contains("desktop_act:conditional", tools);
        Assert.DoesNotContain(tools, tool => tool.StartsWith("desktop_exec:", StringComparison.Ordinal));

        await _http.PutSettingsAsync(new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var offAgain = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        var left = offAgain.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "schedule_create", "schedule_delete", "schedule_list", "web_fetch", "web_search" }, left);
        Assert.Empty(await DesktopToolsAsync());
    }

    [Fact]
    public async Task The_gear_decides_which_tools_ask()
    {
        // The tool table in /api/consumer is what the model can call and what governance does with each.
        async Task<List<string>> ToolsAsync() =>
            (await DesktopToolsAsync()).Select(tool => $"{tool.Name}:{tool.Decision}").ToList();

        await _http.PutSettingsAsync(new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = true, execEnabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var all = await ToolsAsync();
        Assert.Contains("desktop_observe:allow", all);
        Assert.Contains("desktop_clipboard_get:allow", all);
        Assert.Contains("desktop_act:conditional", all);
        Assert.Contains("desktop_exec:ask", all);
        Assert.Contains("desktop_window:ask", all);

        // The middle gear: what a person can watch happen goes through, what leaves the GUI waits.
        var middle = await _http.PostAsJsonAsync("/api/desktop/approval", new { approval = "dangerous" });
        Assert.Equal(HttpStatusCode.OK, middle.StatusCode);

        var dangerous = await ToolsAsync();
        Assert.Contains("desktop_act:conditional", dangerous);
        Assert.Contains("desktop_window:allow", dangerous);
        Assert.Contains("desktop_clipboard_get:allow", dangerous);
        Assert.Contains("desktop_exec:allow", dangerous);
        Assert.Contains("desktop_launch:ask", dangerous);
        Assert.Contains("desktop_clipboard_set:ask", dangerous);

        // And the gear the model was explicitly allowed to run unattended.
        await _http.PostAsJsonAsync("/api/desktop/approval", new { approval = "none" });

        var none = await ToolsAsync();
        Assert.Contains("desktop_exec:allow", none);
        Assert.All(none, tool => Assert.EndsWith(":allow", tool, StringComparison.Ordinal));
        Assert.Contains("task_report:allow", none);
        Assert.Equal(9, none.Count);

        var bogus = await _http.PostAsJsonAsync("/api/desktop/approval", new { approval = "whatever" });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
    }

    [Fact]
    public async Task The_screenshot_tool_exists_only_for_a_model_that_takes_pictures()
    {
        // Sending an image to a text-only model fails the request, so the tool is registered from the claim
        // about the model — not always, and not never.
        async Task<List<string>> ToolsAsync() =>
            (await DesktopToolsAsync()).Select(tool => tool.Name).ToList();
        async Task AssertMatchesRegistryAsync()
        {
            var current = _context.Settings.Load();
            var owner = current.Agents.Owner(AgentScenarios.Desktop)!;
            var registry = new InMemoryToolRegistry();
            AgentRouting.RegisterScenarioTools(AgentScenarios.Desktop, registry, _context, current,
                owner.Model?.SupportsImages ?? current.Model.SupportsImages, _workspace, owner.Id);
            Assert.Equal(registry.Specs.Select(spec => spec.Name), await ToolsAsync());
        }

        await _http.PutSettingsAsync(new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", supportsImages = false },
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        Assert.DoesNotContain("desktop_screenshot", await ToolsAsync());
        await AssertMatchesRegistryAsync();

        await _http.PutSettingsAsync(new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", supportsImages = true },
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var withImages = await ToolsAsync();
        Assert.Contains("desktop_screenshot", withImages);
        await AssertMatchesRegistryAsync();
        // Looking is free whatever the gear says.
        var screenshot = (await DesktopToolsAsync()).First(tool => tool.Name == "desktop_screenshot");
        Assert.Equal("allow", screenshot.Decision);

        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(settings.GetProperty("model").GetProperty("supportsImages").GetBoolean());
    }

    [Fact]
    public async Task Reading_consumer_status_does_not_record_desktop_actions()
    {
        await _http.PutSettingsAsync(new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var before = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");
        var tools = await DesktopToolsAsync();
        await DesktopToolsAsync();
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");

        Assert.Contains(tools, tool => tool.Name == "desktop_act");
        Assert.Contains(tools, tool => tool.Name == "task_report");
        foreach (var field in new[] { "leases", "leaseAsked", "leaseGearAllow" })
        {
            Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
        }
    }

    [Fact]
    public async Task A_desktop_act_decision_rides_the_lease_telemetry()
    {
        // Decide is called directly with an act argument carrying a window id; the lease label is what the
        // exhaustion-rate threshold keys on.
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        try
        {
            var governance = new Partner.Api.DesktopGovernance(_context);
            var before = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");

            var ask = governance.Decide("desktop_act", """{"entityId":"ff","kind":"LeftClick","x":1,"y":1}""");
            Assert.Equal(Partner.Core.Agent.Tools.ToolDecision.Ask, ask.Decision);

            var after = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");
            Assert.True(after.GetProperty("leaseAsked").GetInt32() > before.GetProperty("leaseAsked").GetInt32(),
                $"leaseAsked={after.GetProperty("leaseAsked").GetInt32()}");
        }
        finally
        {
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
        }
    }

    private sealed class QueuedDesktopTool : ITool
    {
        public ToolSpec Spec { get; } = new("desktop_window", "test", "{\"type\":\"object\"}");
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Executed { get; private set; }

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            Queued.SetResult();
            using var mutation = await Partner.Desktop.Tools.DesktopMutationLock.AcquireAsync(ct);
            Executed = true;
            return new ToolResult(true, "executed");
        }
    }

    [Fact]
    public async Task Runner_carries_the_desktop_decision_to_the_queued_mutation()
    {
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, approval = "none", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });
        var dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var (store, _) = JsonlSessionStore.Open(dir);
            await using (store)
            {
                var tool = new QueuedDesktopTool();
                var tools = new InMemoryToolRegistry();
                tools.Register(tool);
                var llm = new ScriptedLlmClient([
                    new LlmResponse(null, [new LlmToolCall("1", "desktop_window", "{}")], "tool_calls"),
                    new LlmResponse("done", [], "stop"),
                ]);
                var runner = new AgentTurnRunner(store, llm, tools, new Partner.Api.DesktopGovernance(_context),
                    new DenyAllApprovalHandler(), new HookRegistry(), tokens: new ApproximateTokenCounter());
                using var held = await Partner.Desktop.Tools.DesktopMutationLock.AcquireAsync();
                var turn = runner.RunAsync(new InputItem
                {
                    ItemId = Guid.NewGuid().ToString(), Seq = 1, Status = InputItemStatus.Running,
                    Source = InputSource.Text, Payload = "test", IdempotencyKey = Guid.NewGuid().ToString(),
                });
                await tool.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await _http.PutSettingsAsync(new
                {
                    desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
                });
                held.Dispose();
                await turn.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(tool.Executed);
            }
        }
        finally
        {
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Desktop_governance_rejects_main_and_stale_scenario_ownership()
    {
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });
        var main = new Partner.Api.DesktopGovernance(_context, mainAgent: true);
        var stale = new Partner.Api.DesktopGovernance(_context, scenario: AgentScenarios.Desktop, agentId: "removed-agent");
        Assert.Equal(ToolDecision.Deny, main.Decide("desktop_act", "{}").Decision);
        Assert.Equal(ToolDecision.Deny, main.Summarize("desktop_act").Decision);
        Assert.Equal(ToolDecision.Deny, stale.Decide("desktop_act", "{}").Decision);
        Assert.Equal(ToolDecision.Deny, stale.Summarize("desktop_act").Decision);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("all", false)]
    [InlineData("all", true)]
    public async Task Queued_desktop_decisions_are_rejected_after_settings_change_without_spending_another_lease(
        string gear, bool useLease)
    {
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, approval = gear, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        const string args = """{"entityId":"ff","kind":"LeftClick","x":1,"y":1}""";
        try
        {
            if (useLease)
                _context.TaskLeases.Grant("ff", args);
            var decision = new Partner.Api.DesktopGovernance(_context).Decide("desktop_act", args);
            Assert.Equal(useLease || gear == "none" ? Partner.Core.Agent.Tools.ToolDecision.Allow : Partner.Core.Agent.Tools.ToolDecision.Ask, decision.Decision);
            Assert.NotNull(decision.IsStillAuthorized);
            var remaining = useLease ? _context.TaskLeases.Snapshot().Single(lease => lease.EntityId == "ff").RemainingSteps : 0;

            using var held = await Partner.Desktop.Tools.DesktopMutationLock.AcquireAsync();
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var action = Task.Run(async () =>
            {
                using var authorization = Partner.Core.Agent.Tools.ToolExecutionAuthorization.Enter(decision.IsStillAuthorized);
                queued.SetResult();
                using var mutation = await Partner.Desktop.Tools.DesktopMutationLock.AcquireAsync();
                return true;
            });
            await queued.Task;
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = true, approval = gear == "all" ? "none" : "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
            // Returning to the original gear must not revive the old decision.
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = true, approval = gear, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
            held.Dispose();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await action);
            Assert.Contains("设置已变化", error.Message, StringComparison.Ordinal);
            if (useLease)
                Assert.Equal(remaining, _context.TaskLeases.Snapshot().Single(lease => lease.EntityId == "ff").RemainingSteps);
        }
        finally
        {
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
        }
    }

    [Fact]
    public async Task Unchanged_desktop_decision_remains_authorized_inside_the_mutation_lock()
    {
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, approval = "none", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });
        try
        {
            var decision = new Partner.Api.DesktopGovernance(_context).Decide("desktop_window", "{}");
            using var authorization = Partner.Core.Agent.Tools.ToolExecutionAuthorization.Enter(decision.IsStillAuthorized);
            using var mutation = await Partner.Desktop.Tools.DesktopMutationLock.AcquireAsync();
        }
        finally
        {
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
        }
    }

    [Fact]
    public async Task A_system_shortcut_asks_even_inside_a_task_lease_and_does_not_spend_it()
    {
        // A lease is "continue working in this window". Win+R opens a run dialog, which is not that window;
        // it must ask on its own and must not consume the steps the human granted for GUI work.
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, approval = "dangerous", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        try
        {
            var governance = new Partner.Api.DesktopGovernance(_context);
            const string shortcut = """{"entityId":"ff","kind":"KeyPress","virtualKey":82,"modifiers":["win"]}""";
            const string ordinary = """{"entityId":"ff","kind":"KeyPress","virtualKey":67,"modifiers":["ctrl"]}""";

            _context.TaskLeases.Grant("ff", shortcut);
            var granted = _context.TaskLeases.Snapshot().Single(lease => lease.EntityId == "ff").RemainingSteps;

            Assert.Equal(
                Partner.Core.Agent.Tools.ToolDecision.Ask,
                governance.Decide("desktop_act", shortcut).Decision);

            // The refused-then-approved shortcut did not spend a step: the grant is still whole.
            Assert.Equal(
                granted,
                _context.TaskLeases.Snapshot().Single(lease => lease.EntityId == "ff").RemainingSteps);

            // Ctrl+C is ordinary GUI work and is still answered by the lease.
            Assert.Equal(
                Partner.Core.Agent.Tools.ToolDecision.Allow,
                governance.Decide("desktop_act", ordinary).Decision);
        }
        finally
        {
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
        }
    }

    [Fact]
    public async Task System_shortcuts_ask_under_the_all_gear_too()
    {
        await _http.PutSettingsAsync(new
        {
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        try
        {
            var governance = new Partner.Api.DesktopGovernance(_context);
            var decision = governance.Decide(
                "desktop_act",
                """{"entityId":"ff","kind":"KeyPress","virtualKey":115,"modifiers":["alt"]}""");

            Assert.Equal(Partner.Core.Agent.Tools.ToolDecision.Ask, decision.Decision);
            Assert.Contains("系统快捷键", decision.Reason, StringComparison.Ordinal);

            // The tool accepts PascalCase JSON; governance must see the same call, not a plain key press.
            var upper = governance.Decide(
                "desktop_act",
                """{"EntityId":"ff","Kind":"KeyPress","VirtualKey":82,"Modifiers":["win"]}""");
            Assert.Equal(Partner.Core.Agent.Tools.ToolDecision.Ask, upper.Decision);
        }
        finally
        {
            await _http.PutSettingsAsync(new
            {
                desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
            });
        }
    }

    [Fact]
    public async Task An_empty_approval_queue_is_reported_rather_than_hidden()
    {
        // The UI has to distinguish "nothing is waiting" from "we could not ask", so the shape is always there.
        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/approvals");

        Assert.True(queue.GetProperty("timeoutSeconds").GetDouble() > 0);
        Assert.Empty(queue.GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task Answering_an_unknown_approval_is_not_found()
    {
        var response = await _http.PostAsJsonAsync("/api/approvals/ap-does-not-exist", new { approved = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_telemetry_window_is_the_retention_it_is_read_from()
    {
        // The window and the retention must be one number: a rate over a different span than reported is a
        // silent error.
        var telemetry = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");

        Assert.Equal(
            telemetry.GetProperty("retentionHours").GetDouble(),
            telemetry.GetProperty("windowHours").GetDouble());

        Assert.Equal(
            _context.Telemetry.Retention.TotalHours,
            telemetry.GetProperty("retentionHours").GetDouble(),
            3);
    }

    [Fact]
    public async Task A_caller_may_narrow_the_telemetry_window_but_not_widen_it()
    {
        // A wider window would report rates over events already evicted, so the request is clamped.
        var narrowed = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry?hoursRaw=0.25");
        Assert.Equal(0.25, narrowed.GetProperty("windowHours").GetDouble());

        var widened = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry?hoursRaw=999");
        Assert.Equal(
            widened.GetProperty("retentionHours").GetDouble(),
            widened.GetProperty("windowHours").GetDouble());

        var negative = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry?hoursRaw=-5");
        Assert.Equal(0, negative.GetProperty("windowHours").GetDouble());
    }

    [Fact]
    public async Task Telemetry_reports_its_shape_before_any_desktop_traffic()
    {
        // An empty sink is a valid answer, not an error: the aggregates exist before the first observe,
        // and every rate reading no candidates is null rather than zero.
        var empty = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");

        Assert.Equal(0, empty.GetProperty("acts").GetInt32());
        Assert.True(empty.GetProperty("snapHitRate").ValueKind is JsonValueKind.Null);
        Assert.Equal(0, empty.GetProperty("walkTruncated").GetInt32());
        Assert.True(empty.GetProperty("observeP50Ms").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task Telemetry_aggregates_carry_the_fields_the_tuning_decisions_read()
    {
        var telemetry = await _http.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");

        foreach (var field in new[] { "acts", "snapHitRate", "verdicts", "pictures", "walkTruncated",
                     "leases", "leaseAsked", "leaseGearAllow",
                     "observeP50Ms", "observeP99Ms", "actP50Ms", "actP99Ms" })
        {
            Assert.True(telemetry.TryGetProperty(field, out _), $"missing {field}");
        }

        foreach (var field in new[] { "anyChange", "noChange", "indeterminate" })
        {
            Assert.True(telemetry.GetProperty("verdicts").TryGetProperty(field, out _), $"missing verdicts.{field}");
        }

        foreach (var field in new[] { "count", "avgWidth", "avgHeight" })
        {
            Assert.True(telemetry.GetProperty("pictures").TryGetProperty(field, out _), $"missing pictures.{field}");
        }
    }
}
