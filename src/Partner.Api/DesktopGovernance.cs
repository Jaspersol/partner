using Partner.Api.Settings;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;

namespace Partner.Api;

/// <summary>
/// Governance for the desktop tools, reading the approval gear as it is **now** rather than as it was when the
/// consumer started.
/// <para>
/// The gear is a decision, not a capability: switching it must not restart a running turn, and it must not
/// tear down a consumer that was started with an injected client (tests, diagnostics) where a restart has no
/// model to start from. Enabling the capability still rebuilds the tool table at start — that one changes what
/// exists.
/// </para>
/// <para>
/// Fail-closed like every other governance: a tool that is not a desktop tool, or a capability that is off,
/// is denied rather than allowed by omission.
/// </para>
/// </summary>
public sealed class DesktopGovernance(ApiContext context) : IToolGovernance
{
    private readonly ApiContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public GovernanceDecision Decide(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!DesktopToolCatalog.All.Contains(toolName, StringComparer.Ordinal))
        {
            return new GovernanceDecision(ToolDecision.Deny, $"'{toolName}' 不是桌面工具（fail-closed）");
        }

        var desktop = _context.Settings.Load().Desktop;
        if (!desktop.Enabled)
        {
            return new GovernanceDecision(ToolDecision.Deny, "桌面能力未启用");
        }

        var gear = ApprovalGears.Normalize(desktop.Approval);

        // Reads never ask; actions ask by gear; what leaves the GUI asks unless the gear says nothing does.
        var needsApproval = DesktopToolCatalog.IsReadOnly(toolName)
            ? false
            : DesktopToolCatalog.IsDangerous(toolName)
                ? gear != ApprovalGears.None
                : gear == ApprovalGears.All;

        return needsApproval
            ? new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' 需要人工批准（挡位：{gear}）")
            : new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在当前挡位下可直接执行（{gear}）");
    }
}
