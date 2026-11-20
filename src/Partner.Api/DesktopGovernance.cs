using Partner.Api.Settings;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;

namespace Partner.Api;

/// <summary>
/// Governance reads current permissions on every call. A turn or job keeps its exposed tool table, while
/// capability and scenario ownership changes prevent subsequent calls through an older table. Pending
/// approvals are invalidated when settings change.
/// <para>
/// The approval gear is a live decision, not a capability that requires rebuilding the consumer.
/// </para>
/// <para>
/// Fail-closed like every other governance: a tool this host does not know is denied rather than allowed by
/// omission. MCP tools are known but not trusted — each one is another process's code — so they ask unless the
/// allow list says otherwise. The gear itself covers every tool that acts, not only the desktop's.
/// </para>
/// </summary>
public sealed class DesktopGovernance(ApiContext context, bool mainAgent = false, string? scenario = null, string? agentId = null, IReadOnlyList<string>? windowTitles = null) : IToolGovernance
{
    private readonly ApiContext _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// The baseline decision for a tool table, not a decision on a particular call. In particular it must not
    /// consume a task lease or record an act; arguments, shortcuts and existing leases can change a real call.
    /// </summary>
    public GovernanceDecision Summarize(string toolName)
    {
        if (toolName != "desktop_act")
        {
            return Decide(toolName, "{}");
        }

        var settings = _context.Settings.Load();
        if (!OwnsTool(settings, toolName))
            return new GovernanceDecision(ToolDecision.Deny, "工具的场景归属已变更");
        var desktop = settings.Desktop;
        if (!desktop.Enabled)
        {
            return new GovernanceDecision(ToolDecision.Deny, "桌面能力未启用");
        }

        var gear = ApprovalGears.Normalize(desktop.Approval);
        return gear == ApprovalGears.All
            ? new GovernanceDecision(ToolDecision.Ask, "桌面动作通常需要人工批准，实际决定取决于动作和租约")
            : new GovernanceDecision(ToolDecision.Allow, "桌面动作的实际决定取决于动作和租约");
    }

    public GovernanceDecision Decide(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var (settings, revision) = _context.Settings.Snapshot();
        if (!OwnsTool(settings, toolName))
            return new GovernanceDecision(ToolDecision.Deny, "工具的场景归属已变更");
        var decision = DecideCore(toolName, argumentsJson, settings);
        return decision with { IsStillAuthorized = () => _context.Settings.Snapshot().Revision == revision };
    }

    private bool OwnsTool(RuntimeSettings settings, string toolName)
    {
        var kind = Agents.AgentRouting.ClassifyNativeTool(toolName);
        if (mainAgent)
            return kind switch
            {
                Agents.AgentRouting.NativeToolKind.Desktop => false,
                Agents.AgentRouting.NativeToolKind.File => Agents.AgentRouting.MainOwnsFiles(settings),
                Agents.AgentRouting.NativeToolKind.Web => Agents.AgentRouting.MainOwnsWeb(settings),
                _ => true,
            };
        if (scenario is null || agentId is null || kind is not (Agents.AgentRouting.NativeToolKind.File
            or Agents.AgentRouting.NativeToolKind.Web or Agents.AgentRouting.NativeToolKind.Desktop))
            return true;
        // Desktop tools hold the window policy they started with. Do not let an old job use that policy
        // after it changes; a new job receives the updated one without restarting the consumer.
        if (kind == Agents.AgentRouting.NativeToolKind.Desktop && windowTitles is not null
            && !windowTitles.SequenceEqual(settings.Desktop.AllowedWindowTitles, StringComparer.Ordinal))
            return false;
        var route = Agents.AgentRouting.Find(settings, scenario);
        return route is not null && route.Agent.Id == agentId
            && Agents.AgentRouting.ScenarioTools(scenario, settings,
                route.Agent.Model?.SupportsImages ?? settings.Model.SupportsImages).Contains(toolName, StringComparer.Ordinal);
    }

    private GovernanceDecision DecideCore(string toolName, string argumentsJson, RuntimeSettings settings)
    {
        var desktop = settings.Desktop;
        var gear = ApprovalGears.Normalize(desktop.Approval);

        if (NamespacedMcpTool.TryUnprefix(toolName, out _, out _))
        {
            // An MCP tool is someone else's code in a process this host started. It is asked for unless the
            // allow list says otherwise, and deliberately not "allowed because the user enabled the server":
            // enabling says which tools to *offer*, not which may act.
            var mcp = _context.Settings.Load().Mcp;
            if (!mcp.Enabled)
            {
                return new GovernanceDecision(ToolDecision.Deny, "MCP 未启用");
            }

            return mcp.AllowedTools.Contains(toolName, StringComparer.Ordinal)
                ? new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在允许清单中")
                : new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' 需要人工批准（外部进程）");
        }

        var kind = Agents.AgentRouting.ClassifyNativeTool(toolName);

        // The delegation tools and the sub-agent's report tool only move words: starting a job does nothing to
        // the machine, and the job's own tools answer to this same governance when they run.
        if (kind == Agents.AgentRouting.NativeToolKind.Information)
        {
            return new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 只传递信息");
        }

        // Timed tasks. Reading them is harmless.
        if (kind == Agents.AgentRouting.NativeToolKind.ScheduleList)
        {
            return new GovernanceDecision(ToolDecision.Allow, "'schedule_list' 只读");
        }

        // Creating can be undone by deleting; deleting cannot. So the dangerous gear asks for the delete, and
        // only the strictest gear asks for both.
        if (kind is Agents.AgentRouting.NativeToolKind.ScheduleCreate or Agents.AgentRouting.NativeToolKind.ScheduleDelete)
        {
            var deleting = kind == Agents.AgentRouting.NativeToolKind.ScheduleDelete;
            var asks = deleting ? gear != ApprovalGears.None : gear == ApprovalGears.All;

            if (!asks)
            {
                return new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在当前挡位下可直接执行（{gear}）");
            }

            return new GovernanceDecision(
                ToolDecision.Ask,
                deleting
                    ? "'schedule_delete' 会删掉一个已存在的定时任务，且无法恢复，需要你批准"
                    : "'schedule_create' 会让助手之后按时收到一条消息并照做，需要你批准");
        }

        if (toolName is "file_write" or "file_edit")
            return !Agents.AgentRouting.FileWriteAvailable(settings)
                ? new GovernanceDecision(ToolDecision.Deny, "文件写入未启用或不可用")
                : gear == ApprovalGears.None
                    ? new GovernanceDecision(ToolDecision.Allow, "当前挡位不审批文件写入或编辑")
                    : new GovernanceDecision(ToolDecision.Ask, "文件写入或编辑需要人工批准");

        // Reading is read-only and never asks, like the desktop's own reads; what bounds it is the directory
        // whitelist inside the tool. The switch is checked live so turning files off takes effect at once.
        if (kind == Agents.AgentRouting.NativeToolKind.File)
        {
            return _context.Settings.Load().Files.Enabled
                ? new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 只读，限定在允许的目录内")
                : new GovernanceDecision(ToolDecision.Deny, "读文件未启用");
        }

        // Web access: read-only on other people's servers, so it never asks on its own — but a search sends the
        // query, and therefore the user's own words, to a third party, while a fetch is the model choosing a
        // destination. The switch covers both, and both read it live so turning it off takes effect at once.
        if (kind == Agents.AgentRouting.NativeToolKind.Web)
        {
            return WebDecision(toolName, argumentsJson, gear);
        }

        if (kind != Agents.AgentRouting.NativeToolKind.Desktop)
        {
            return new GovernanceDecision(ToolDecision.Deny, $"'{toolName}' 不是桌面工具（fail-closed）");
        }

        if (!desktop.Enabled)
        {
            return new GovernanceDecision(ToolDecision.Deny, "桌面能力未启用");
        }

        if (toolName == "desktop_exec")
        {
            return !desktop.ExecEnabled
                ? new GovernanceDecision(ToolDecision.Deny, "命令执行未启用")
                : gear == ApprovalGears.All
                    ? new GovernanceDecision(ToolDecision.Ask, "执行命令具有系统权限，需要人工批准")
                    : new GovernanceDecision(ToolDecision.Allow, "当前挡位不审批命令执行");
        }

        // Reads never ask; actions ask by gear; what leaves the GUI asks unless the gear says nothing does.
        var needsApproval = DesktopToolCatalog.IsReadOnly(toolName)
            ? false
            : DesktopToolCatalog.IsDangerous(toolName)
                ? gear != ApprovalGears.None
                : gear == ApprovalGears.All;

        if (toolName == "desktop_act")
        {
            // A system combination leaves the window the human approved. It asks under every gear except none,
            // and deliberately never reaches TaskLeaseStore: continuing work on one window is not permission to
            // open a run dialog or lock the session.
            if (SystemShortcutGuard.DescribeBlocked(argumentsJson) is { } blocked)
            {
                var shortcutDecision = gear == ApprovalGears.None
                    ? new GovernanceDecision(ToolDecision.Allow,
                        $"{blocked}：当前挡位不审批（{gear}），但这次动作不会获得任务租约")
                    : new GovernanceDecision(ToolDecision.Ask,
                        $"{blocked}：系统快捷键会离开当前窗口，需要单独批准（挡位：{gear}；批准不会授予任务租约）");

                RecordLease(shortcutDecision, argumentsJson);
                return shortcutDecision;
            }

            var decision = SwitchOnTaskLease(toolName, argumentsJson, needsApproval, gear);
            RecordLease(decision, argumentsJson);
            return decision;
        }

        return needsApproval
            ? new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' 需要人工批准（挡位：{gear}）")
            : new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' 在当前挡位下可直接执行（{gear}）");
    }

    /// <summary>
    /// A search is allowed whenever the capability is on. A fetch asks, and only under the strictest gear, for a
    /// host this session never saw: a host from the person's own words is a lead being followed, any other is the
    /// model picking a destination.
    /// </summary>
    private GovernanceDecision WebDecision(string toolName, string argumentsJson, string gear)
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
            : gear == ApprovalGears.All
                ? new GovernanceDecision(ToolDecision.Ask, $"要打开 '{host}'：这个地址不在本次会话的搜索结果里，需要你确认")
                : new GovernanceDecision(ToolDecision.Allow, $"'web_fetch' 在当前挡位下可直接执行（{gear}）");
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
    /// A GUI action first answers to what a human already granted for its window. The grant is spent one step
    /// at a time by <see cref="TaskLeaseStore.Consume"/>: the call that decides may run also spends it.
    /// <para>
    /// The dangerous tools never reach this: they asked by gear before the lease existed, and a grant of
    /// continuing GUI latitude must not widen into launching or pasting — that is a separate ask, every time.
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
