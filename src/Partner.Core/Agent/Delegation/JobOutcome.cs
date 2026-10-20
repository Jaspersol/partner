using System.Text;

namespace Partner.Core.Agent.Delegation;

/// <summary>Why a job could not be carried out. These are the values the main agent is told.</summary>
public static class JobCategories
{
    /// <summary>Every round was spent and none succeeded.</summary>
    public const string RoundsExhausted = "rounds_exhausted";

    /// <summary>The person said no to an approval, or nobody answered it. Retrying would only ask again.</summary>
    public const string ApprovalDenied = "approval_denied";

    /// <summary>A rule refused it: governance, a path outside the whitelist, an elevated window.</summary>
    public const string GovernanceDenied = "governance_denied";

    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
    public const string ModelError = "model_error";
    public const string InternalError = "internal_error";

    /// <summary>The sub-agent itself said another round cannot help.</summary>
    public const string ReportedImpossible = "reported_impossible";

    /// <summary>The process stopped while the job was running.</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>What one round came to.</summary>
/// <param name="Round">1-based.</param>
/// <param name="Success">Whether the sub-agent reported success with evidence.</param>
/// <param name="Reason">A short, code-written reason for a failed round (never the model's own claim).</param>
/// <param name="Error">The last error text seen in the round, truncated; empty when there was none.</param>
/// <param name="Actions">The tool calls the round made, as <c>name ×n</c> pieces.</param>
public sealed record RoundRecord(int Round, bool Success, string Reason, string Error, IReadOnlyList<string> Actions);

/// <summary>
/// How a job ended, in the shape the main agent is told.
/// </summary>
/// <param name="Completed">True when a round reported success.</param>
/// <param name="Category">For a failure: one of <see cref="JobCategories"/>. Null on success.</param>
/// <param name="Summary">The success summary, or the last failure summary.</param>
/// <param name="Evidence">What the success rests on.</param>
/// <param name="Error">The last error, verbatim up to a limit.</param>
/// <param name="Rounds">How many rounds were run.</param>
/// <param name="MaxRounds">How many were allowed.</param>
/// <param name="History">One record per round run.</param>
/// <param name="Actions">Every tool call across the job, as <c>name ×n</c> pieces.</param>
public sealed record JobOutcome(
    bool Completed,
    string? Category,
    string Summary,
    string? Evidence,
    string? Error,
    int Rounds,
    int MaxRounds,
    IReadOnlyList<RoundRecord> History,
    IReadOnlyList<string> Actions)
{
    /// <summary>The most of a sub-agent's conclusion that is sent to the main agent whole.</summary>
    public const int SummaryChars = 1500;

    /// <summary>The most of its evidence. Evidence is what a web agent copies out of pages, and it can be long.</summary>
    public const int EvidenceChars = 2000;

    /// <summary>
    /// A report is read into the main agent's context, so it is bounded; the cut says where the rest is.
    /// </summary>
    private static string Limit(string? text, int max, string jobId)
    {
        if (text is null || text.Length <= max)
        {
            return text ?? string.Empty;
        }

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut]
            + $"…（已截断，原文共 {text.Length} 字；完整内容在任务 {jobId} 的子会话里，GET /api/agent-jobs/{jobId}/session 可读）";
    }

    /// <summary>
    /// The message the main agent receives. The first line is the discriminator: <c>已完成</c> or <c>无法执行</c>,
    /// so a model (or a person reading the log) can tell the two apart without reading further.
    /// </summary>
    public string ToMessage(string jobId, string agentName, string goal)
    {
        var text = new StringBuilder();
        if (Completed)
        {
            text.Line($"[子 agent「{agentName}」任务 {jobId} · 已完成 · 第 {Rounds}/{MaxRounds} 轮]");
            text.Line($"目标：{goal}");
            text.Line($"结论：{Limit(Summary, SummaryChars, jobId)}");
            text.Append($"证据：{Limit(Evidence, EvidenceChars, jobId)}");
            return text.ToString();
        }

        text.Line($"[子 agent「{agentName}」任务 {jobId} · 无法执行]");
        text.Line($"目标：{goal}");
        text.Line($"原因类别：{Category}");
        text.Line($"错误：{(string.IsNullOrWhiteSpace(Error) ? Summary : Error)}");
        text.Line($"已尝试：{Rounds}/{MaxRounds} 轮");
        foreach (var round in History)
        {
            text.Line($"  {round.Round}. {round.Reason}");
        }

        if (Actions.Count > 0)
        {
            text.Append("已执行过的动作：").Append(string.Join("，", Actions));
        }

        return text.ToString().TrimEnd();
    }
}
