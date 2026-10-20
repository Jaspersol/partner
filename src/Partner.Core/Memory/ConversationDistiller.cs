using System.Text;

namespace Partner.Core.Memory;

/// <summary>One thing worth remembering, before it becomes a record.</summary>
/// <param name="Body">The text to remember, in the reader's words rather than a paraphrase.</param>
/// <param name="Importance">0..1. How much this shapes later turns.</param>
/// <param name="Veracity">0..1. How sure we are it is still true.</param>
/// <param name="SourceEventSeq">The session event it came from, so the same event cannot be distilled twice.</param>
public sealed record DistilledMemory(string Body, double Importance, double Veracity, long SourceEventSeq);

/// <summary>
/// Turns a turn's worth of conversation into things worth remembering.
/// <para>
/// Deliberately <b>not</b> a model call. The tier system already answers "what survives": a distilled memory
/// lands in <see cref="MemoryTier.Working"/>, is scored on recall, is promoted to an episodic copy only after
/// <c>PromotionAge</c>, and is trimmed if it never matures. A second model call to pre-filter would add latency
/// and a failure mode, and a wrong answer would be unrecoverable where a noisy record is one
/// <c>SoftDeleteAsync</c> away.
/// </para>
/// <para>
/// Filters to turns that carry a <em>statement about the user or the work</em>: acknowledgements, unanswered
/// questions, and questions about the assistant itself are not facts about the person.
/// </para>
/// </summary>
public static class ConversationDistiller
{
    /// <summary>Longest body kept per memory. Long enough for a fact, short enough to be re-injected.</summary>
    public const int MaxBodyLength = 400;

    /// <summary>
    /// Turns shorter than this carry no statement worth keeping. Public because a hand-written memory must clear
    /// the same bar.
    /// </summary>
    public const int MinBodyLength = 4;

    /// <summary>
    /// Phrases that mark a question <em>about the assistant</em> rather than a fact about the user. The next turn
    /// already has the assistant's own description in the system prompt.
    /// </summary>
    private static readonly string[] SelfReferential =
    {
        "你是谁", "你叫什么", "你能做什么", "你会什么", "你能干什么",
        "你是ai", "你是机器人", "你是什么",
        "who are you", "what can you do", "your name",
    };

    /// <summary>Phrases that mark a passing exchange with nothing to keep.</summary>
    private static readonly string[] Chitchat =
    {
        "谢谢", "多谢", "好的", "收到", "明白了", "知道了", "辛苦了", "再见",
        "thank", "thanks", "ok", "okay", "got it", "hi", "hello", "bye",
    };

    /// <summary>What to remember from one turn: the user's message and the assistant's reply.</summary>
    /// <param name="userText">What the person said this turn.</param>
    /// <param name="assistantText">What the model answered, which often names the thing in play.</param>
    /// <param name="sourceEventSeq">The event the exchange starts at.</param>
    /// <param name="now">Injected rather than read from the clock, so a test can date a record.</param>
    public static DistilledMemory? Distill(
        string userText,
        string assistantText,
        long sourceEventSeq,
        DateTimeOffset now)
    {
        var user = (userText ?? string.Empty).Trim();
        if (user.Length < MinBodyLength)
        {
            return null;
        }

        if (IsSelfReferential(user) || IsChitchat(user))
        {
            return null;
        }

        // A question with no answer attached is a question, not a fact.
        var answer = (assistantText ?? string.Empty).Trim();
        if (answer.Length == 0)
        {
            return null;
        }

        var body = answer.Length >= user.Length ? answer : $"{user}\n{answer}";
        body = body.Length <= MaxBodyLength ? body : body[..MaxBodyLength] + "…";

        return new DistilledMemory(
            body,
            Importance: Importance(user, answer),
            Veracity: 1.0,
            SourceEventSeq: sourceEventSeq);
    }

    private static bool IsSelfReferential(string text)
    {
        var lower = text.ToLowerInvariant();
        return SelfReferential.Any(phrase => lower.Contains(phrase, StringComparison.Ordinal));
    }

    private static bool IsChitchat(string text)
    {
        var trimmed = text.TrimEnd('。', '.', '！', '!', '？', '?');
        return Chitchat.Any(phrase => trimmed.Equals(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// How much a turn shapes later ones, read off its own wording. A stated preference, an ongoing project, or
    /// an instruction to always do something outweighs an answer to a one-off question. Nothing here guesses at
    /// importance from length.
    /// </summary>
    private static double Importance(string user, string answer)
    {
        var score = 0.4;

        foreach (var marker in new[] { "我喜欢", "我不喜欢", "我讨厌", "记住", "以后", "一直", "永远", "我叫", "我是", "我在用", "我的" })
        {
            if (user.Contains(marker, StringComparison.Ordinal))
            {
                score += 0.15;
                break;
            }
        }

        foreach (var marker in new[] { "总是", "不要", "必须", "只能", "注意", "注意：" })
        {
            if (answer.Contains(marker, StringComparison.Ordinal))
            {
                score += 0.1;
                break;
            }
        }

        return Math.Clamp(score, 0.0, 1.0);
    }
}
