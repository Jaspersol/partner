using Partner.Api.Settings;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;

namespace Partner.Api;

/// <summary>
/// Governance for the tools this host exposes, reading the approval gear as it is **now** rather than as it
/// was when the consumer started.
/// <para>
/// The gear is a decision, not a capability: switching it must not restart a running turn, and it must not
/// tear down a consumer that was started with an injected client (tests, diagnostics) where a restart has no
/// model to start from. Enabling a capability still rebuilds the tool table at start — that one changes what
/// exists.
/// </para>
/// <para>
/// Fail-closed like every other governance: a tool this host does not know is denied rather than allowed by
/// omission. MCP tools are known but not trusted — each one is another process's code — so they ask unless the
/// allow list says otherwise.
/// </para>
/// </summary>
public sealed class DesktopGovernance(ApiContext context) : IToolGovernance
{
    private readonly ApiContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public GovernanceDecision Decide(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (NamespacedMcpTool.TryUnprefix(toolName, out _, out _))
        {
            // An MCP tool is someone else's code in a process this host started. It is asked for unless the
            // allow list says otherwise — the same reading as the desktop gear, and deliberately not "allowed
            // because the user enabled the server": enabling says which tools to *offer*, not which may act.
            var mcp = _context.Settings.Load().Mcp;
            if (!mcp.Enabled)
            {
                return new GovernanceDecision(ToolDecision.Deny, "MCP 未启用");
            }

            return mcp.AllowedTools.Contains(toolName, StringComparer.Ordinal)
                ? new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在允许清单中")
                : new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' 需要人工批准（外部进程）");
        }

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

        if (toolName == "desktop_act")
        {
            return SwitchOnTaskLease(toolName, argumentsJson, needsApproval, gear);
        }

        return needsApproval
            ? new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' 需要人工批准（挡位：{gear}）")
            : new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在当前挡位下可直接执行（{gear}）");
    }

    /// <summary>
    /// A GUI action first answers to what a human already granted for its window. The grant is spent one
    /// step at a time, by <see cref="TaskLeaseStore.Consume"/> — the same call that decides may run also
    /// spends it, because Decide is exactly where an execution about to happen passes through.
    /// <para>
    /// The dangerous tools never reach this: they asked by gear before the lease existed, and a grant of
    /// continuing GUI latitude must not quietly widen into launching or pasting — that is a separate ask,
    /// every time.
    /// </para>
    /// </summary>
    private GovernanceDecision SwitchOnTaskLease(string toolName, string argumentsJson, bool needsApproval, string gear)
    {
        var target = TaskLeaseStore.TryTarget(argumentsJson);
        if (target is not null)
        {
            var (allowed, reason, remaining) = _context.TaskLeases.Consume(target.Value.EntityId, target.Value.Kind, argumentsJson);
            if (allowed)
            {
                return new GovernanceDecision(ToolDecision.Allow,
                    $"在任务租约内：窗口 {target.Value.EntityId} 已获准继续操作（{reason}）");
            }
        }

        return needsApproval
            ? new GovernanceDecision(ToolDecision.Ask,
                $"'{toolName}' 需要人工批准（挡位：{gear}；批准将授权同一窗口继续 {TaskLeaseStore.DefaultSteps} 步）")
            : new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在当前挡位下可直接执行（{gear}）");
    }
}
