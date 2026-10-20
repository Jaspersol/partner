using System.Text.Json;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Delegation;

/// <summary>What a sub-agent said about one round, as it called <c>task_report</c>.</summary>
/// <param name="Success">True for <c>status=success</c>.</param>
/// <param name="Summary">The result (success) or how far it got (failure).</param>
/// <param name="Evidence">What the claim rests on: a verify result, a screenshot id, a quoted line.</param>
/// <param name="Error">The specific error, for a failure.</param>
/// <param name="Retryable">False when the sub-agent says another round cannot help.</param>
public sealed record TaskReport(bool Success, string Summary, string? Evidence, string? Error, bool Retryable);

/// <summary>
/// The tool a sub-agent ends a round with. Registered only in a sub-agent's registry — the main agent never
/// sees it.
/// <para>
/// A round's verdict comes from this call, not from the wording of the last message: parsing prose for "I
/// did it" is how a failure gets reported as a success. A round that ends without calling it is a failed round.
/// </para>
/// </summary>
public sealed class TaskReportTool : ITool
{
    public const string ToolName = "task_report";

    private readonly object _gate = new();
    private TaskReport? _latest;

    public ToolSpec Spec { get; } = new(
        ToolName,
        "结束本轮时必须调用：报告任务成功还是失败。成功要写 evidence（依据）；失败要写 error（具体错误）。"
        + "如果确认再试也不会成功，加 retryable=false。调用之后不要再调用其他工具。",
        """{"type":"object","properties":{"status":{"type":"string","enum":["success","failed"]},"summary":{"type":"string","description":"结果；失败时写做到了哪一步"},"evidence":{"type":"string","description":"依据：最后一次校验结果、截图编号、读到的原文位置"},"error":{"type":"string","description":"失败时的具体错误"},"retryable":{"type":"boolean","description":"确认再试也不会成功时填 false"}},"required":["status","summary"]}""");

    /// <summary>The last report of the current round, or null when the round has not reported.</summary>
    public TaskReport? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Forgets the previous round's report, so a round that never reports is not credited with the last one's.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _latest = null;
        }
    }

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;

            var status = Text(root, "status");
            if (status is not ("success" or "failed"))
            {
                return Refuse("status 必须是 success 或 failed");
            }

            var summary = Text(root, "summary");
            if (string.IsNullOrWhiteSpace(summary))
            {
                return Refuse("需要 summary");
            }

            var evidence = Text(root, "evidence");
            var error = Text(root, "error");
            if (status == "success" && string.IsNullOrWhiteSpace(evidence))
            {
                return Refuse("success 需要 evidence：没有依据的成功不被接受。拿不出依据就报 failed 并说明");
            }

            if (status == "failed" && string.IsNullOrWhiteSpace(error))
            {
                return Refuse("failed 需要 error：写清具体的错误");
            }

            var retryable = !(root.TryGetProperty("retryable", out var flag) && flag.ValueKind == JsonValueKind.False);
            lock (_gate)
            {
                _latest = new TaskReport(status == "success", summary!, evidence, error, retryable);
            }

            return Task.FromResult(new ToolResult(true, "汇报已记录。本轮结束，不要再调用其他工具。"));
        }
        catch (JsonException error)
        {
            return Refuse($"参数不是合法 JSON（{error.Message}）");
        }
    }

    private static Task<ToolResult> Refuse(string reason) =>
        Task.FromResult(new ToolResult(false, $"invalid_args: {reason}"));

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
