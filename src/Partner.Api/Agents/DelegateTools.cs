using System.Text;
using System.Text.Json;
using Partner.Core.Agent;
using Partner.Core.Agent.Delegation;
using Partner.Core.Agent.Tools;

namespace Partner.Api.Agents;

/// <summary>
/// Hands a piece of work to a sub-agent. Returns the moment the job is started.
/// <para>
/// The scenario list in the schema and in the description comes from the scenarios that are reachable when the
/// consumer starts; a change to that set rebuilds the tool table, so the enum cannot offer a scenario that has
/// no one behind it.
/// </para>
/// </summary>
public sealed class AgentDelegateTool : ITool
{
    public const string ToolName = DelegationTools.Delegate;

    private readonly SubAgentHost _host;

    public AgentDelegateTool(SubAgentHost host, IReadOnlyList<DelegationScenario> scenarios)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(scenarios);

        var keys = JsonSerializer.Serialize(scenarios.Select(scenario => scenario.Key));
        Spec = new ToolSpec(
            ToolName,
            "把一件事交给子 agent 去做，立刻返回，不会等它做完；结果稍后作为一条以“[子 agent”开头的消息回来。可用场景："
            + string.Join("；", scenarios.Select(scenario => $"{scenario.Key}={scenario.Name}（{scenario.When}）")),
            """{"type":"object","properties":{"scenario":{"type":"string","enum":"""
            + keys
            + """},"goal":{"type":"string","description":"要子 agent 做什么，写完整：它看不到你和用户的对话"},"context":{"type":"string","description":"可选：它做这件事需要知道的背景"},"attachments":{"type":"array","items":{"type":"string"},"description":"可选：相关文件的绝对路径（如用户发来的文档），子 agent 会按路径读取"}},"required":["scenario","goal"]}""");
    }

    private static string? WithAttachments(string? context, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return context;
        }

        var listed = "相关文件（按路径读取）：\n" + string.Join("\n", paths.Select(path => $"- {path}"));
        return string.IsNullOrWhiteSpace(context) ? listed : context + "\n\n" + listed;
    }

    public ToolSpec Spec { get; }

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        if (!Delegation.TryRead(argumentsJson, out var scenario, out var goal, out var context, out var problem))
        {
            return new ToolResult(false, $"invalid_args: {problem}");
        }

        // The sub-agent cannot see this conversation, and a goal like "read the file we just talked about" is
        // meaningless to it. The last few turns ride along as background so such references resolve.
        context = WithAttachments(context, Delegation.Strings(argumentsJson, "attachments"));
        var background = await _host.RecentConversationAsync(ct).ConfigureAwait(false);
        var result = _host.Start(scenario, goal, context, background);
        if (result.Started)
        {
            var job = result.Job!;
            var where = job.Status == AgentJobStatus.Queued
                ? $"任务 {job.Id} 在排队（前面还有 {job.Ahead} 个任务，轮到它时才开始，最多 {job.MaxRounds} 轮）"
                : $"任务 {job.Id} 已开始（最多 {job.MaxRounds} 轮）";
            return new ToolResult(
                true,
                $"已交给「{job.AgentName}」，{where}。结果会作为一条消息回到这里。"
                + "现在先告诉用户已经交出去了，不要等。");
        }

        return new ToolResult(false, $"{(result.Busy ? "busy" : "refused")}: {result.Refusal}");
    }
}

/// <summary>Reads how far a job has got.</summary>
public sealed class AgentJobStatusTool(SubAgentHost host) : ITool
{
    public const string ToolName = "agent_job_status";

    public ToolSpec Spec { get; } = new(
        ToolName,
        "查看子 agent 任务的进度。不填 jobId 就列出最近的任务。",
        """{"type":"object","properties":{"jobId":{"type":"string"}}}""");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var jobId = Delegation.Text(argumentsJson, "jobId");
        if (jobId is not null)
        {
            return Task.FromResult(host.Job(jobId) is { } job
                ? new ToolResult(true, Describe(job))
                : new ToolResult(false, $"not_found: 没有任务 {jobId}"));
        }

        var recent = host.Jobs().Take(5).ToList();
        return Task.FromResult(recent.Count == 0
            ? new ToolResult(true, "没有子 agent 任务。")
            : new ToolResult(true, string.Join("\n", recent.Select(Describe))));
    }

    internal static string Describe(AgentJobView job)
    {
        var text = new StringBuilder($"任务 {job.Id}（{job.AgentName}）· {job.Status}");
        text.Append(job.Status == AgentJobStatus.Running ? $" · 第 {job.Round}/{job.MaxRounds} 轮" : job.Status == AgentJobStatus.Queued ? $" · 前面还有 {job.Ahead} 个任务" : $" · {job.Round}/{job.MaxRounds} 轮");
        if (job.Category is not null)
        {
            text.Append($" · {job.Category}");
        }

        if (job.Activity is { Length: > 0 } activity)
        {
            text.Append($"\n正在：{activity}");
        }

        text.Append($"\n目标：{job.Goal}");
        foreach (var round in job.Rounds)
        {
            text.Append("\n  ").Append(round);
        }

        return text.ToString();
    }
}

/// <summary>Stops a running job.</summary>
public sealed class AgentJobCancelTool(SubAgentHost host) : ITool
{
    public const string ToolName = "agent_job_cancel";

    public ToolSpec Spec { get; } = new(
        ToolName,
        "取消一个正在进行的子 agent 任务。",
        """{"type":"object","properties":{"jobId":{"type":"string"}},"required":["jobId"]}""");

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var jobId = Delegation.Text(argumentsJson, "jobId");
        if (jobId is null)
        {
            return new ToolResult(false, "invalid_args: 需要 jobId");
        }

        return await host.CancelAsync(jobId).ConfigureAwait(false)
            ? new ToolResult(true, $"已请求取消任务 {jobId}；结束后会有一条汇报回来。")
            : new ToolResult(false, $"not_found: 任务 {jobId} 不存在，或已经结束");
    }
}

internal static class Delegation
{
    /// <summary>A JSON array of strings, or empty when the property is missing or not one.</summary>
    public static IReadOnlyList<string> Strings(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    .Select(item => item.GetString()!.Trim())
                    .Take(10)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool TryRead(string json, out string scenario, out string goal, out string? context, out string problem)
    {
        scenario = Text(json, "scenario") ?? string.Empty;
        goal = Text(json, "goal") ?? string.Empty;
        context = Text(json, "context");
        problem = scenario.Length == 0 ? "需要 scenario" : goal.Length == 0 ? "需要 goal" : string.Empty;
        return problem.Length == 0;
    }

    public static string? Text(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
