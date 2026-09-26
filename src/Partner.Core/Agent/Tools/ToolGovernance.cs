namespace Partner.Core.Agent.Tools;

/// <summary>一次工具调用的治理结论。</summary>
public enum ToolDecision
{
    /// <summary>直接执行。</summary>
    Allow,

    /// <summary>需要人工批准后才能执行。</summary>
    Ask,

    /// <summary>拒绝执行。</summary>
    Deny,
}

/// <summary>
/// 治理结论与原因。<see cref="Reason"/> 在所有分支上都必须非空——它会被写进日志/事件，用于事后解释。
/// </summary>
/// <param name="Decision">结论。</param>
/// <param name="Reason">非空的判定原因。</param>
public sealed record GovernanceDecision(ToolDecision Decision, string Reason);

/// <summary>
/// 工具治理（设计文档 §2.9.6）：在工具真正执行<b>之前</b>判定允许、询问还是拒绝。
/// 实现不得产生副作用；<paramref name="argumentsJson"/> 供将来按参数细粒度判定使用。
/// </summary>
public interface IToolGovernance
{
    /// <param name="toolName">模型请求的工具名。</param>
    /// <param name="argumentsJson">模型给出的原始参数 JSON。</param>
    GovernanceDecision Decide(string toolName, string argumentsJson);
}

/// <summary>
/// 白名单治理：默认失败关闭（fail-closed），未显式列出的工具一律拒绝（设计文档 §5 R8）。
/// <para>
/// 判定顺序：<c>allowed</c> → <see cref="ToolDecision.Allow"/>；否则 <c>ask</c> → <see cref="ToolDecision.Ask"/>；
/// 否则 <see cref="ToolDecision.Deny"/>。同名同时出现在两个列表时 <c>allowed</c> 优先（<see cref="ToolDecision.Allow"/> 胜出）。
/// 工具名按序号（大小写敏感）比较。
/// </para>
/// </summary>
public sealed class AllowListGovernance : IToolGovernance
{
    private readonly HashSet<string> _allowed;
    private readonly HashSet<string> _ask;

    /// <param name="allowed">无需批准即可执行的工具名。</param>
    /// <param name="ask">需要人工批准的工具名；为 <see langword="null"/> 时视为空。</param>
    public AllowListGovernance(IEnumerable<string> allowed, IEnumerable<string>? ask = null)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        _allowed = new HashSet<string>(allowed, StringComparer.Ordinal);
        _ask = new HashSet<string>(ask ?? [], StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public GovernanceDecision Decide(string toolName, string argumentsJson)
    {
        // 统一优先级（与 PolicyToolGovernance 一致）：deny > ask > allow > 默认拒绝。
        // 更严格者胜出：同时出现在 ask 与 allow 时按 ask 处理，否则「需要批准」的设置会被静默绕过。
        if (_ask.Contains(toolName))
        {
            return new GovernanceDecision(ToolDecision.Ask, "需要人工批准（治理询问列表）");
        }

        if (_allowed.Contains(toolName))
        {
            return new GovernanceDecision(ToolDecision.Allow, "在治理白名单中");
        }

        return new GovernanceDecision(ToolDecision.Deny, "not registered in the governance allow-list");
    }
}
