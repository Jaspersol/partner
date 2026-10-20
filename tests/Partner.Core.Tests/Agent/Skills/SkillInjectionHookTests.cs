using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Skills;
using Partner.Core.Contracts;

namespace Partner.Core.Tests.Agent.Skills;

/// <summary>
/// The skill injection hook: injected text, registry order, no injection for an empty set, priority and cancellation.
/// </summary>
public sealed class SkillInjectionHookTests
{
    private const string Header = "Available skills (load one with the skill tool before using it):";

    [Fact]
    public async Task RunAsync_InjectsOneBlockWithBothSkillsInRegistryOrder()
    {
        var registry = new InMemorySkillRegistry();
        registry.Reload([Skill("zeta", "Zeta", "Zeta desc"), Skill("alpha", "Alpha", "Alpha desc")]);
        var hook = new SkillInjectionHook(registry);
        var ctx = NewContext();

        var outcome = await hook.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(HookOutcome.Continue, outcome);
        Assert.Equal(AgentPhase.PreAgentBuild, hook.Phase);
        Assert.Equal("skills.inject", hook.Name);
        Assert.Equal(50, hook.Priority);

        // Registry order is SkillId ascending, so alpha comes first.
        var block = Assert.Single(ctx.InjectedPrompts);
        Assert.Equal(
            Header + "\n- alpha — Alpha: Alpha desc\n- zeta — Zeta: Zeta desc",
            block);
    }

    [Fact]
    public async Task RunAsync_EmptyRegistry_InjectsNothing()
    {
        var hook = new SkillInjectionHook(new InMemorySkillRegistry());
        var ctx = NewContext();

        var outcome = await hook.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(HookOutcome.Continue, outcome);
        Assert.Empty(ctx.InjectedPrompts);
    }

    [Fact]
    public async Task RunAsync_InjectsAtPriority50()
    {
        var registry = new InMemorySkillRegistry();
        registry.Reload([Skill("alpha", "Alpha", "Alpha desc")]);
        var hook = new SkillInjectionHook(registry);
        var ctx = NewContext();
        ctx.Inject("later", priority: 60);

        await hook.RunAsync(ctx, CancellationToken.None);

        // 50 < 60: the skill block lands before the prompt injected later.
        Assert.Equal(2, ctx.InjectedPrompts.Count);
        Assert.StartsWith(Header, ctx.InjectedPrompts[0]);
        Assert.Equal("later", ctx.InjectedPrompts[1]);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_Throws()
    {
        var registry = new InMemorySkillRegistry();
        registry.Reload([Skill("alpha", "Alpha", "Alpha desc")]);
        var hook = new SkillInjectionHook(registry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hook.RunAsync(NewContext(), cts.Token).AsTask());
    }

    private static HookContext NewContext() => new()
    {
        SessionId = "session-1",
        Source = InputSource.Text,
        InputPayload = "hello",
        WorkspaceDirectory = Path.GetTempPath(),
    };

    private static SkillDefinition Skill(string skillId, string name, string description) => new()
    {
        SkillId = skillId,
        Name = name,
        Description = description,
        Directory = Path.Combine(Path.GetTempPath(), "partner-skills-tests", skillId),
        Instructions = "body",
    };
}
