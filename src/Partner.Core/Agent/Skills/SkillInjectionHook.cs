using System.Text;
using Partner.Core.Agent.Hooks;

namespace Partner.Core.Agent.Skills;

/// <summary>
/// Injects the current skill list into the system prompt.
/// <para>The injected format is a contract other modules depend on:</para>
/// <code>
/// Available skills (load one with the skill tool before using it):
/// - &lt;skillId&gt; — &lt;name&gt;: &lt;description&gt;
/// </code>
/// <para>
/// Lines follow registry order (that is, <see cref="SkillDefinition.SkillId"/> ascending); an empty registry
/// injects nothing, so no <c>system</c> event is written for an empty list.
/// </para>
/// </summary>
public sealed class SkillInjectionHook : AgentHookBase
{
    private const string Header = "Available skills (load one with the skill tool before using it):";

    private readonly ISkillRegistry _registry;

    /// <summary>Creates the hook.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public SkillInjectionHook(ISkillRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public override AgentPhase Phase => AgentPhase.PreAgentBuild;

    /// <inheritdoc />
    public override string Name => "skills.inject";

    /// <inheritdoc />
    public override int Priority => 50;

    /// <inheritdoc />
    public override ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        var skills = _registry.Skills;
        if (skills.Count == 0)
        {
            // No skills, no injection: an empty list should not produce a system event.
            return ValueTask.FromResult(HookOutcome.Continue);
        }

        var block = new StringBuilder(Header);
        foreach (var skill in skills)
        {
            block.Append('\n')
                .Append("- ").Append(skill.SkillId)
                .Append(" — ").Append(skill.Name)
                .Append(": ").Append(skill.Description);
        }

        ctx.Inject(block.ToString(), Priority);
        return ValueTask.FromResult(HookOutcome.Continue);
    }
}
