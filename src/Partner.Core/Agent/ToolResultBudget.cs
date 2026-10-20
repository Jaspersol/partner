using Partner.Core.Agent.Llm;
using Partner.Core.Memory;

namespace Partner.Core.Agent;

/// <summary>
/// The budget that binds <i>inside</i> a turn. The assembler only prices the history a turn starts from; once
/// the tool loop is running, every result is appended whole, and one large <c>file_read</c> or <c>web_fetch</c>
/// is enough to outgrow the window and fail the next model call.
/// <para>
/// Two rules, applied by the runner: a single result is capped (head and tail kept, the middle folded into a
/// marker that says how to read more), and when the conversation as a whole nears the window the oldest tool
/// results are replaced by a one-line note, never the newest ones the model is still working from.
/// </para>
/// </summary>
public static class ToolResultBudget
{
    /// <summary>A result may take at most this share of the prompt budget (one fifth).</summary>
    public const int ResultShareDivisor = 5;

    /// <summary>Floor for the per-result cap, so a tiny window still lets a result say something.</summary>
    public const int MinResultTokens = 512;

    /// <summary>Past this share of the prompt budget, older tool results start being folded.</summary>
    public const double CollapseAbove = 0.8;

    /// <summary>Folding stops once the conversation is back under this share, so it does not fold again next step.</summary>
    public const double CollapseTo = 0.6;

    /// <summary>A result already this small is not worth folding.</summary>
    private const int FoldableTokens = 64;

    private const string FoldedMark = "[较早的工具结果已折叠";

    /// <summary>The cap on one tool result for a window with <paramref name="promptBudget"/> tokens of room.</summary>
    public static int ResultCap(int promptBudget) => Math.Max(MinResultTokens, promptBudget / ResultShareDivisor);

    /// <summary>
    /// Caps <paramref name="content"/> at <paramref name="maxTokens"/>: the head and the tail survive, the
    /// middle becomes a marker. The head gets three quarters of the room (a listing or a document is read from
    /// the top) and the tail the rest (an error or a total usually sits at the end).
    /// </summary>
    public static string Clamp(string? content, int maxTokens, ITokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);

        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var total = counter.Count(content);
        if (maxTokens <= 0 || total <= maxTokens)
        {
            return content;
        }

        var headTokens = Math.Max(1, maxTokens * 3 / 4);
        var tailTokens = Math.Max(1, maxTokens - headTokens);
        var head = Longest(content, headTokens, counter, fromEnd: false);
        var tail = Longest(content, tailTokens, counter, fromEnd: true);

        if (head + tail >= content.Length)
        {
            return content;
        }

        var kept = counter.Count(content[..head]) + counter.Count(content[^tail..]);
        var marker = $"\n\n…[工具结果过长，已截断：原文约 {total} token，省略中间约 {Math.Max(0, total - kept)} token。"
            + "需要更多内容时，用更小的 offset/limit 或更窄的参数再读一次。]\n\n";

        return content[..head] + marker + content[^tail..];
    }

    /// <summary>
    /// Folds the oldest tool results among <c>conversation[..protectFrom]</c> once the conversation is above
    /// <see cref="CollapseAbove"/> of <paramref name="promptBudget"/>, until it is back under
    /// <see cref="CollapseTo"/>. Returns how many results were folded.
    /// <para>
    /// Everything from <paramref name="protectFrom"/> on is the latest step and is never touched: the model has
    /// not answered it yet. The call/result pairing is kept — only the result body is replaced — because a
    /// provider rejects a tool call without its result.
    /// </para>
    /// </summary>
    public static int CollapseOlder(
        List<LlmMessage> conversation,
        int protectFrom,
        int promptBudget,
        ITokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(counter);

        var total = Measure(conversation, counter);
        if (total <= promptBudget * CollapseAbove)
        {
            return 0;
        }

        var target = promptBudget * CollapseTo;
        var folded = 0;
        var limit = Math.Min(protectFrom, conversation.Count);

        for (var i = 0; i < limit && total > target; i++)
        {
            var message = conversation[i];
            if (message.Role != "tool"
                || string.IsNullOrEmpty(message.Content)
                || message.Content.StartsWith(FoldedMark, StringComparison.Ordinal))
            {
                continue;
            }

            var cost = counter.Count(message.Content);
            if (cost <= FoldableTokens)
            {
                continue;
            }

            var note = $"{FoldedMark}：{message.Name ?? "tool"}，原约 {cost} token。需要时请重新调用该工具。]";
            conversation[i] = message with { Content = note };
            total -= cost - counter.Count(note);
            folded++;
        }

        return folded;
    }

    /// <summary>The conversation's size: text, tool-call arguments, framing and a flat price per image.</summary>
    public static int Measure(IReadOnlyList<LlmMessage> conversation, ITokenCounter counter)
    {
        var total = 0;
        foreach (var message in conversation)
        {
            total += ContextBudget.MessageOverheadTokens;

            if (!string.IsNullOrEmpty(message.Content))
            {
                total += counter.Count(message.Content);
            }

            if (message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    total += counter.Count(call.Name) + counter.Count(call.ArgumentsJson);
                }
            }

            if (message.Images is { Count: > 0 } images)
            {
                total += images.Count * ContextBudget.ImageTokens;
            }
        }

        return total;
    }

    /// <summary>The longest prefix (or suffix) of <paramref name="text"/> whose count stays within <paramref name="tokens"/>.</summary>
    private static int Longest(string text, int tokens, ITokenCounter counter, bool fromEnd)
    {
        var low = 0;
        var high = text.Length;

        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            var piece = fromEnd ? text[^mid..] : text[..mid];

            if (counter.Count(piece) <= tokens)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }
}
