using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;

namespace Partner.Api.Tests;

/// <summary>
/// MCP wiring: the tool table, the governance boundary, and the settings round-trip.
/// <para>
/// The client itself is already covered by <c>StdioMcpClientTests</c> in Core. What is untested until now is
/// the part that was missing entirely — whether anything calls it — so these are about the host, not the
/// protocol.
/// </para>
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
        // Fail-closed, and the previous behaviour was to deny anything that was not a desktop tool — which
        // meant an MCP tool, however well-behaved, could never run at all. The capability has to be on for the
        // tool to be reachable in the first place; the off case is its own test below.
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
        // Switching the server off has to actually stop it, not merely stop offering it.

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
        // The MCP branch must not swallow the tools it was added alongside: reads stay ungated, actions ask.
        await using var context = await ApiContext.OpenAsync(Workspace("mcp-desktop"));
        context.Settings.Save(context.Settings.Load() with
        {
            Desktop = new DesktopSettings { Enabled = true, Approval = ApprovalGears.All },
            Mcp = new McpSettings { Enabled = true },
        });

        var governance = new DesktopGovernance(context);

        // desktop_observe only looks, so it is allowed; desktop_exec acts, so it asks. That difference is the
        // desktop gear working, and it has to survive the branch added above.
        Assert.Equal(ToolDecision.Allow, governance.Decide("desktop_observe", "{}").Decision);
        Assert.Equal(ToolDecision.Ask, governance.Decide("desktop_exec", "{}").Decision);
        Assert.Equal(ToolDecision.Ask, governance.Decide("mcp_files_read", "{}").Decision);
    }
}
