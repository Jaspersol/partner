namespace Partner.Core.Agent.Hooks;

/// <summary>
/// 钩子对当前阶段流程的裁决（设计文档 §2.9.3）。
/// </summary>
public enum HookOutcome
{
    /// <summary>继续执行本阶段的后续钩子。</summary>
    Continue,

    /// <summary>立即停止本阶段，后续钩子不再运行。</summary>
    ShortCircuit,

    /// <summary>标记本轮跳过 Agent 执行；本阶段后续钩子仍继续运行（粘性标志，直到 <c>ResetTurn</c>）。</summary>
    SkipAgent,
}
