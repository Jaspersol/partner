using Partner.Core.Agent.Governance;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Governance;

/// <summary>
/// The tool governance policy: fail-closed defaults, unified precedence, and target-parameter rules
/// (design doc §2.9.6 / §8.5 gap A).
/// </summary>
public sealed class ToolPolicyTests
{
    private const string Workspace = @"C:\work\partner";

    private const string FullPolicy = """
        {
          "version": 1,
          "tools": {
            "clock":       { "decision": "allow" },
            "desktop.act": { "decision": "ask", "rules": { "requiredWindowTitles": ["Notepad"] } },
            "file.write":  { "decision": "allow", "rules": { "allowedPathPrefixes": ["${workspace}"] } },
            "http.get":    { "decision": "allow", "rules": { "allowedHosts": ["localhost", "example.com"] } },
            "shell.exec":  { "decision": "allow", "rules": { "allowedCommands": ["git", "dotnet"] } },
            "rm.rf":       { "decision": "deny" }
          }
        }
        """;

    [Fact]
    public void Parse_reads_every_entry_and_decision()
    {
        var policy = ToolPolicy.Parse(FullPolicy, Workspace);

        Assert.Equal(ToolDecision.Allow, policy.Find("clock")!.Decision);
        Assert.Equal(ToolDecision.Ask, policy.Find("desktop.act")!.Decision);
        Assert.Equal(ToolDecision.Deny, policy.Find("rm.rf")!.Decision);
        Assert.Null(policy.Find("not-listed"));
    }

    [Fact]
    public void Workspace_placeholder_is_expanded()
    {
        var policy = ToolPolicy.Parse(FullPolicy, Workspace);

        var prefixes = policy.Find("file.write")!.Rules!.AllowedPathPrefixes!;
        Assert.Equal(Path.GetFullPath(Workspace), Assert.Single(prefixes));
    }

    [Fact]
    public void Workspace_placeholder_without_a_workspace_fails_loudly()
    {
        var ex = Assert.Throws<ArgumentException>(() => ToolPolicy.Parse(FullPolicy));
        Assert.Contains("${workspace}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_decision_fails_loudly()
    {
        const string json = """{ "tools": { "x": { "decision": "maybe" } } }""";

        var ex = Assert.Throws<ArgumentException>(() => ToolPolicy.Parse(json));
        Assert.Contains("maybe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_fails_loudly()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ToolPolicy.Parse("{ not json"));
    }

    [Fact]
    public void Empty_name_fails_loudly()
    {
        const string json = """{ "tools": { "  ": { "decision": "allow" } } }""";

        Assert.Throws<ArgumentException>(() => ToolPolicy.Parse(json));
    }

    [Fact]
    public void No_tools_yields_the_deny_everything_policy()
    {
        var policy = ToolPolicy.Parse("""{ "version": 1, "tools": {} }""");

        Assert.Empty(policy.Entries);
        Assert.Null(policy.Find("anything"));
    }

    [Fact]
    public void Policy_loads_from_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"partner-policy-{Guid.NewGuid():n}.json");
        try
        {
            File.WriteAllText(path, FullPolicy);
            var policy = ToolPolicy.Load(path, Workspace);
            Assert.Equal(ToolDecision.Ask, policy.Find("desktop.act")!.Decision);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class PolicyToolGovernanceTests
{
    private const string Workspace = @"C:\work\partner";

    private static ToolPolicy Policy(string json) => ToolPolicy.Parse(json, Workspace);

    [Fact]
    public void Unlisted_tool_is_denied_fail_closed()
    {
        var governance = new PolicyToolGovernance(Policy("""{ "tools": { "clock": { "decision": "allow" } } }"""));

        var decision = governance.Decide("surprise", "{}");

        Assert.Equal(ToolDecision.Deny, decision.Decision);
        Assert.Contains("fail-closed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_deny_wins()
    {
        var governance = new PolicyToolGovernance(Policy("""{ "tools": { "shell.exec": { "decision": "deny" } } }"""));

        var decision = governance.Decide("shell.exec", "{}");

        Assert.Equal(ToolDecision.Deny, decision.Decision);
        Assert.Contains("explicitly denied", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Allow_and_ask_are_returned_with_reasons()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "clock": { "decision": "allow" }, "desktop.act": { "decision": "ask" } } }
            """));

        Assert.Equal(ToolDecision.Allow, governance.Decide("clock", "{}").Decision);
        Assert.Equal(ToolDecision.Ask, governance.Decide("desktop.act", "{}").Decision);
        Assert.All(
            new[] { governance.Decide("clock", "{}"), governance.Decide("desktop.act", "{}") },
            d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
    }

    [Fact]
    public void Path_inside_the_workspace_is_allowed_and_outside_is_denied()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "file.write": { "decision": "allow", "rules": { "allowedPathPrefixes": ["${workspace}"] } } } }
            """));

        var inside = governance.Decide("file.write", """{ "path": "C:\\work\\partner\\notes.txt" }""");
        var outside = governance.Decide("file.write", """{ "path": "C:\\Windows\\system32\\drivers\\etc\\hosts" }""");

        Assert.Equal(ToolDecision.Allow, inside.Decision);
        Assert.Equal(ToolDecision.Deny, outside.Decision);
        Assert.Contains("outside the allowed prefixes", outside.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_traversal_out_of_the_workspace_is_denied()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "file.write": { "decision": "allow", "rules": { "allowedPathPrefixes": ["${workspace}"] } } } }
            """));

        var decision = governance.Decide("file.write", """{ "path": "C:\\work\\partner\\..\\..\\Windows\\hosts" }""");

        Assert.Equal(ToolDecision.Deny, decision.Decision);
    }

    [Fact]
    public void Missing_target_field_is_denied_when_a_rule_requires_it()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "file.write": { "decision": "allow", "rules": { "allowedPathPrefixes": ["${workspace}"] } } } }
            """));

        var decision = governance.Decide("file.write", "{}");

        Assert.Equal(ToolDecision.Deny, decision.Decision);
        Assert.Contains("target path is missing", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_allowlist_accepts_exact_and_subdomains_only()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "http.get": { "decision": "allow", "rules": { "allowedHosts": ["example.com"] } } } }
            """));

        Assert.Equal(ToolDecision.Allow, governance.Decide("http.get", """{ "url": "https://example.com/x" }""").Decision);
        Assert.Equal(ToolDecision.Allow, governance.Decide("http.get", """{ "url": "https://api.example.com/x" }""").Decision);
        Assert.Equal(ToolDecision.Deny, governance.Decide("http.get", """{ "url": "https://evil.com/x" }""").Decision);
        Assert.Equal(ToolDecision.Deny, governance.Decide("http.get", """{ "url": "https://example.com.evil.com/x" }""").Decision);
    }

    [Fact]
    public void Command_allowlist_rejects_unlisted_and_shell_metacharacters()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "shell.exec": { "decision": "allow", "rules": { "allowedCommands": ["git", "dotnet"] } } } }
            """));

        Assert.Equal(ToolDecision.Allow, governance.Decide("shell.exec", """{ "command": "git status" }""").Decision);
        Assert.Equal(ToolDecision.Allow, governance.Decide("shell.exec", """{ "command": "dotnet test" }""").Decision);
        Assert.Equal(ToolDecision.Deny, governance.Decide("shell.exec", """{ "command": "rm -rf /" }""").Decision);
        Assert.Equal(
            ToolDecision.Deny,
            governance.Decide("shell.exec", """{ "command": "git status; rm -rf /" }""").Decision);
        Assert.Contains(
            "metacharacters",
            governance.Decide("shell.exec", """{ "command": "git status && whoami" }""").Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Window_title_rule_matches_case_insensitively()
    {
        var governance = new PolicyToolGovernance(Policy("""
            { "tools": { "desktop.act": { "decision": "allow", "rules": { "requiredWindowTitles": ["Notepad"] } } } }
            """));

        Assert.Equal(
            ToolDecision.Allow,
            governance.Decide("desktop.act", """{ "windowTitle": "untitled - notepad" }""").Decision);
        Assert.Equal(
            ToolDecision.Deny,
            governance.Decide("desktop.act", """{ "windowTitle": "PowerShell" }""").Decision);
    }

    [Fact]
    public void Rules_are_skipped_when_the_entry_has_none()
    {
        var governance = new PolicyToolGovernance(Policy("""{ "tools": { "clock": { "decision": "allow" } } }"""));

        Assert.Equal(ToolDecision.Allow, governance.Decide("clock", "not even json").Decision);
    }
}
