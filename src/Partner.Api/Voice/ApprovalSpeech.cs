using Partner.Core.Agent.Tools;

namespace Partner.Api.Voice;

/// <summary>What an utterance means when an approval prompt is waiting for an answer.</summary>
internal enum ApprovalIntent
{
    /// <summary>Not an approval command; it is an ordinary message.</summary>
    None,

    Approve,

    Deny,
}

/// <summary>
/// The spoken side of an approval request: a short prompt that names the asker and the action, and the exact
/// commands the person may answer with.
/// <para>
/// The prompt never reads the tool's raw arguments. They can contain paths, clipboard contents and other text
/// that should not be read into a room, and they are frequently long enough to bury the question.
/// </para>
/// <para>
/// The commands are matched as whole utterances. "批准" inside "不批准" must not approve anything, and a
/// sentence that merely mentions approval belongs to the model, not to governance.
/// </para>
/// </summary>
internal static class ApprovalSpeech
{
    private const int MaxOriginChars = 36;

    private static readonly Dictionary<string, string> ToolLabels = new(StringComparer.Ordinal)
    {
        ["desktop_act"] = "桌面操作",
        ["desktop_window"] = "窗口操作",
        ["desktop_launch"] = "启动程序",
        ["desktop_exec"] = "执行命令",
        ["desktop_clipboard_set"] = "写入剪贴板",
        ["desktop_clipboard_get"] = "读取剪贴板",
        ["desktop_observe"] = "观察屏幕",
        ["desktop_screenshot"] = "屏幕截图",
        ["desktop_wait"] = "等待界面",
        ["schedule_create"] = "创建定时任务",
        ["schedule_delete"] = "删除定时任务",
        ["schedule_list"] = "查看定时任务",
        ["web_fetch"] = "访问网页",
        ["web_search"] = "网页搜索",
        ["agent_delegate"] = "启动子任务",
        ["agent_job_status"] = "查看子任务",
        ["agent_job_cancel"] = "取消子任务",
        ["file_read"] = "读取文件",
        ["file_list"] = "列出文件",
    };

    // Approval is the irreversible direction, so only the phrases the prompt actually teaches are accepted.
    // A refusal cannot cause an action, so the denial list can cover the natural variants.
    private static readonly HashSet<string> ApproveCommands = new(StringComparer.Ordinal)
    {
        "批准执行",
        "批准",
    };

    private static readonly HashSet<string> DenyCommands = new(StringComparer.Ordinal)
    {
        "拒绝执行",
        "拒绝",
        "不允许执行",
        "不批准执行",
        "不同意执行",
        "不同意",
        "不批准",
        "不要执行",
        "取消",
    };

    /// <summary>The one or two sentences read out when a request arrives.</summary>
    public static string Prompt(PendingApproval request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var label = Label(request.ToolName);
        var who = string.IsNullOrWhiteSpace(request.Origin)
            ? string.Empty
            : $"{Shorten(request.Origin!)}。";

        return $"{who}需要你批准：{label}。说「批准执行」或「拒绝执行」。";
    }

    /// <summary>What is said after the answer was accepted.</summary>
    public static string Result(PendingApproval request, bool approved, int remaining)
    {
        ArgumentNullException.ThrowIfNull(request);

        var label = Label(request.ToolName);
        var result = approved ? $"已批准：{label}。" : $"已拒绝：{label}。";
        return remaining > 0 ? $"{result}还有 {remaining} 个请求待批准。" : result;
    }

    /// <summary>Another answer path (the banner, or a second voice command) settled the request first.</summary>
    public static string Settled() => "这个请求已经处理过了。";

    /// <summary>The request timed out before the answer arrived.</summary>
    public static string Expired() => "这个请求已经过期了。";

    /// <summary>
    /// Classifies a whole utterance. Punctuation and whitespace are ignored; nothing else is. In particular,
    /// this is not a "contains" search: a longer sentence that happens to contain a command is a message.
    /// <para>
    /// The active wake word is stripped when the block contains it: the KWS catch-up window deliberately starts
    /// before the hit, so "小克小克，批准执行" can arrive as one transcript. Only the keyword that actually
    /// opened the session is removed, never arbitrary leading speech.
    /// </para>
    /// </summary>
    public static ApprovalIntent Classify(string? transcript, string? wakeWord = null)
    {
        var text = Normalize(transcript);
        var wake = Normalize(wakeWord);

        if (wake.Length > 0)
        {
            // A repeated keyword is possible when the hit arrives late; strip a few occurrences, not the rest
            // of the sentence.
            for (var i = 0; i < 4 && text.StartsWith(wake, StringComparison.Ordinal); i++)
            {
                text = text[wake.Length..];
            }
        }

        if (text.Length == 0)
        {
            return ApprovalIntent.None;
        }

        // Denial first: it is the safer answer, and it keeps a command set that grows later from shadowing it.
        if (DenyCommands.Contains(text))
        {
            return ApprovalIntent.Deny;
        }

        return ApproveCommands.Contains(text) ? ApprovalIntent.Approve : ApprovalIntent.None;
    }

    /// <summary>The spoken label for a tool; unknown names stay readable instead of being guessed at.</summary>
    public static string Label(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "未知操作";
        }

        return ToolLabels.TryGetValue(toolName, out var label) ? label : toolName;
    }

    /// <summary>Lower-cases nothing and translates nothing: the same rules the ASR transcript is compared by.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || IsPunctuation(c))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsPunctuation(char c) =>
        c is '。' or '，' or '、' or '！' or '？' or '；' or '：'
            or '!' or '?' or ',' or '.' or ';' or ':' or '"' or '\'' or '“' or '”' or '‘' or '’'
            or '（' or '）' or '(' or ')' or '【' or '】' or '[' or ']' or '《' or '》'
            or '…' or '—' or '-';

    private static string Shorten(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= MaxOriginChars ? single : single[..MaxOriginChars] + "…";
    }
}
