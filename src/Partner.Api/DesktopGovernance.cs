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

        // The delegation tools and the sub-agent's report tool only move words: starting a job does nothing to
        // the machine, and the job's own tools answer to this same governance when they run.
        if (toolName is Agents.AgentDelegateTool.ToolName or Agents.AgentJobStatusTool.ToolName
            or Agents.AgentJobCancelTool.ToolName or Partner.Core.Agent.Delegation.TaskReportTool.ToolName)
        {
            return new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 只传递信息");
        }

        // Reading is read-only and never asks, like the desktop's own reads; what bounds it is the directory
        // whitelist inside the tool. The switch is checked live so turning files off takes effect at once.
        if (toolName is "file_list" or "file_read")
        {
            return _context.Settings.Load().Files.Enabled
                ? new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 只读，限定在允许的目录内")
                : new GovernanceDecision(ToolDecision.Deny, "读文件未启用");
        }

        // Web access: read-only on other people's servers, so it never asks on its own — but a search sends the
        // query, and therefore the user's own words, to a third party, while a fetch is the model choosing a
        // destination. The switch covers both, and both read it live so turning it off takes effect at once.
        if (toolName is Partner.Core.Agent.Web.WebSearchTool.Name or Partner.Core.Agent.Web.WebFetchTool.Name)
        {
            return WebDecision(toolName, argumentsJson);
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
            var decision = SwitchOnTaskLease(toolName, argumentsJson, needsApproval, gear);
            RecordLease(decision, argumentsJson);
            return decision;
        }

        return needsApproval
            ? new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' 需要人工批准（挡位：{gear}）")
            : new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在当前挡位下可直接执行（{gear}）");
    }

    /// <summary>
    /// Search is allowed whenever the capability is on; fetching is allowed only for a host this session has
    /// already seen. The distinction is who chose the address: a host that came out of the person's own question
    /// or out of a search it started is a lead being followed, while any other host is the model picking a
    /// destination and sending a request to it — which is the user's decision the first time, not the model's.
    /// </summary>
    private GovernanceDecision WebDecision(string toolName, string argumentsJson)
    {
        var web = _context.Web;
        if (!_context.Settings.Load().Web.Enabled)
        {
            return new GovernanceDecision(ToolDecision.Deny, "联网搜索未启用");
        }

        if (toolName == Partner.Core.Agent.Web.WebSearchTool.Name)
        {
            return new GovernanceDecision(ToolDecision.Allow, "'web_search' 已启用（结果只是线索，需自行核对）");
        }

        var host = HostOf(argumentsJson);
        if (host is null)
        {
            // The tool refuses the call itself with a precise reason; letting it do so keeps one wording for
            // "this address is unusable" instead of two that drift apart.
            return new GovernanceDecision(ToolDecision.Allow, "'web_fetch' 的地址由工具自行校验");
        }

        return web.Ledger.Saw(host)
            ? new GovernanceDecision(ToolDecision.Allow, $"'{host}' 出现在本次会话的搜索结果或用户消息中")
            : new GovernanceDecision(ToolDecision.Ask, $"要打开 '{host}'：这个地址不在本次会话的搜索结果里，需要你确认");
    }

    /// <summary>The host of the URL argument, or null when there is nothing to judge.</summary>
    private static string? HostOf(string argumentsJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("url", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var raw = value.GetString();
            return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri.Host : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The act decision rides the telemetry stream, keyed by what answered: in-lease (and the steps that
    /// remain), ask, or the decision the gear itself made. The exhaustion rate — the number the lease-step
    /// tuning waits for — is acts answered with nothing left versus acts answered at all.
    /// </summary>
    private void RecordLease(GovernanceDecision decision, string argumentsJson)
    {
        if (_context.Telemetry is not { } sink)
        {
            return;
        }

        string label;
        if (decision.Decision == ToolDecision.Allow && decision.Reason.Contains("任务租约", StringComparison.Ordinal))
        {
            label = "inLease";
        }
        else if (decision.Decision == ToolDecision.Ask)
        {
            label = "asked";
        }
        else
        {
            label = "gearAllow";
        }

        try
        {
            var entityId = TaskLeaseStore.TryTarget(argumentsJson)?.EntityId;
            sink.Record(new Partner.Desktop.Telemetry.DesktopTelemetryEvent(
                DateTimeOffset.UtcNow,
                "governance_desktop_act",
                label,
                entityId,
                Ok: decision.Decision != ToolDecision.Deny,
                Snapped: null,
                Verify: null,
                MediaWidth: null,
                MediaHeight: null,
                LatencyMs: 0,
                StopReason: null));
        }
        catch (Exception)
        {
            // Telemetry must not fail the decision being reported.
        }
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
