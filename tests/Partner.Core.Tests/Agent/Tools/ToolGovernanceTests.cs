using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Tools;

public sealed class ToolGovernanceTests
{
    [Fact]
    public void Decide_UnlistedToolIsDeniedWithNonEmptyReason()
    {
        var governance = new AllowListGovernance(new[] { "read_file" });

        var decision = governance.Decide("delete_everything", "{}");

        Assert.Equal(ToolDecision.Deny, decision.Decision);
        Assert.Equal("not registered in the governance allow-list", decision.Reason);
    }

    [Fact]
    public void Decide_AllowListAllowsAndAskListAsks()
    {
        var governance = new AllowListGovernance(new[] { "read_file" }, new[] { "write_file" });

        var allowed = governance.Decide("read_file", "{\"path\":\"a.txt\"}");
        Assert.Equal(ToolDecision.Allow, allowed.Decision);
        Assert.False(string.IsNullOrWhiteSpace(allowed.Reason));

        var asked = governance.Decide("write_file", "{\"path\":\"a.txt\"}");
        Assert.Equal(ToolDecision.Ask, asked.Decision);
        Assert.False(string.IsNullOrWhiteSpace(asked.Reason));

        // 治理只看工具名：未知工具仍失败关闭。
        Assert.Equal(ToolDecision.Deny, governance.Decide("shell", "{}").Decision);
    }

    [Fact]
    public void Decide_EmptyAllowListDeniesEverything()
    {
        var governance = new AllowListGovernance(Array.Empty<string>());

        Assert.Equal(ToolDecision.Deny, governance.Decide("read_file", "{}").Decision);
        Assert.Equal(ToolDecision.Deny, governance.Decide("shell", "{}").Decision);
        Assert.Equal(ToolDecision.Deny, governance.Decide(string.Empty, "{}").Decision);
    }

    [Fact]
    public void Decide_NameInBothListsAsks()
    {
        // Unified precedence: deny > ask > allow > default deny — the stricter rule wins, otherwise an
        // "approval required" entry could be silently bypassed by also listing the tool as allowed.
        var governance = new AllowListGovernance(new[] { "shell" }, new[] { "shell" });

        Assert.Equal(ToolDecision.Ask, governance.Decide("shell", "{}").Decision);
    }

    [Fact]
    public void InMemoryToolRegistry_RegistersInOrderAndFindsByName()
    {
        var registry = new InMemoryToolRegistry();
        var alpha = new FakeTool("alpha");
        var beta = new FakeTool("beta");

        registry.Register(alpha);
        registry.Register(beta);

        Assert.Equal(new[] { "alpha", "beta" }, registry.Specs.Select(s => s.Name));
        Assert.Same(alpha, registry.Find("alpha"));
        Assert.Same(beta, registry.Find("beta"));
        Assert.Null(registry.Find("gamma"));
        Assert.Null(registry.Find(string.Empty));
    }

    [Fact]
    public void InMemoryToolRegistry_RejectsDuplicateNameAndNullToolWithoutChangingState()
    {
        var registry = new InMemoryToolRegistry();
        registry.Register(new FakeTool("alpha"));

        var duplicate = Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeTool("alpha")));
        Assert.False(string.IsNullOrWhiteSpace(duplicate.Message));
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));

        Assert.Equal("alpha", Assert.Single(registry.Specs).Name);
    }

    [Fact]
    public void InMemoryToolRegistry_SpecsExposeRegisteredToolMetadata()
    {
        var registry = new InMemoryToolRegistry();
        var tool = new FakeTool("echo");
        registry.Register(tool);

        var spec = Assert.Single(registry.Specs);
        Assert.Same(tool.Spec, spec);
        Assert.Equal("echo", spec.Name);
        Assert.Equal("{\"type\":\"object\"}", spec.ParametersJsonSchema);
    }

    [Fact]
    public async Task DenyAllApprovalHandler_NeverApproves()
    {
        IApprovalHandler handler = new DenyAllApprovalHandler();

        Assert.False(await handler.RequestAsync("shell", "{\"cmd\":\"rm -rf /\"}"));
        Assert.False(await handler.RequestAsync("write_file", "{}", CancellationToken.None));
    }

    private sealed class FakeTool(string name) : ITool
    {
        public ToolSpec Spec { get; } = new(name, $"{name} 工具", "{\"type\":\"object\"}");

        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
            => Task.FromResult(new ToolResult(true, argumentsJson));
    }
}
