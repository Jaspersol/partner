namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Agent 生命周期钩子（设计文档 §2.9.3）。实现者通常继承 <see cref="AgentHookBase"/> 以省略约束列表。
/// </summary>
public interface IAgentHook
{
    /// <summary>本钩子挂载的阶段。</summary>
    AgentPhase Phase { get; }

    /// <summary>阶段内唯一的名字，用于 <see cref="Before"/>/<see cref="After"/> 约束。</summary>
    string Name { get; }

    /// <summary>排序权重，越小越先执行（默认 100）。仅在无约束冲突时作为决胜条件。</summary>
    int Priority { get; }

    /// <summary>必须先于这些钩子（同阶段内按 <see cref="Name"/> 引用）运行。</summary>
    IReadOnlyList<string> Before { get; }

    /// <summary>必须晚于这些钩子（同阶段内按 <see cref="Name"/> 引用）运行。</summary>
    IReadOnlyList<string> After { get; }

    /// <summary>执行钩子。</summary>
    ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct);
}
