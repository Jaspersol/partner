namespace Partner.Core.Agent.Hooks;

/// <summary>
/// 一轮 Agent 执行中可挂载钩子的阶段（设计文档 §2.9.3）。
/// </summary>
public enum AgentPhase
{
    /// <summary>输入分发之前。</summary>
    PreDispatch,

    /// <summary>输入分发之后。</summary>
    PostDispatch,

    /// <summary>构建 Agent（装配 prompt/工具）之前。</summary>
    PreAgentBuild,

    /// <summary>构建 Agent 之后。</summary>
    PostAgentBuild,

    /// <summary>执行 Agent（调用模型）之前。</summary>
    PreExecute,

    /// <summary>响应产出之后。</summary>
    PostResponse,

    /// <summary>本轮发生错误时。</summary>
    OnError,

    /// <summary>无论成败都会执行的收尾阶段。</summary>
    Finally,
}
