using System.Text;
using Partner.Core.Agent.Hooks;

namespace Partner.Core.Agent.Skills;

/// <summary>
/// 把当前技能清单注入系统提示的钩子（设计文档缺口 C：Skills 的注入格式）。
/// <para>注入格式是其它模块依赖的契约：</para>
/// <code>
/// Available skills (load one with the skill tool before using it):
/// - &lt;skillId&gt; — &lt;name&gt;: &lt;description&gt;
/// </code>
/// <para>
/// 行按注册表顺序（即 <see cref="SkillDefinition.SkillId"/> 升序）拼接；
/// 注册表为空时什么都不注入，因此不会为空列表写出 <c>system</c> 事件。
/// </para>
/// </summary>
public sealed class SkillInjectionHook : AgentHookBase
{
    private const string Header = "Available skills (load one with the skill tool before using it):";

    private readonly ISkillRegistry _registry;

    /// <summary>创建钩子。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> 为 <see langword="null"/>。</exception>
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
            // 没有技能就不注入：空列表不应产生 system 事件。
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
