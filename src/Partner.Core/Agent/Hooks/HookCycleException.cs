namespace Partner.Core.Agent.Hooks;

/// <summary>
/// 同一阶段内钩子的 <see cref="IAgentHook.Before"/>/<see cref="IAgentHook.After"/> 约束成环。
/// 在 <see cref="HookRegistry.Register"/> 时即被检出，且该钩子不会被注册。
/// </summary>
public sealed class HookCycleException : InvalidOperationException
{
    /// <summary>用消息构造异常（消息中至少包含一个成环钩子的名字）。</summary>
    public HookCycleException(string message)
        : base(message)
    {
    }
}
