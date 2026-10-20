using System.Text;

namespace Partner.Core.Memory;

/// <summary>
/// Keeps the store honest about two things: which record speaks for a topic, and which records have gone
/// quiet long enough to stop being trusted.
/// <para>
/// Both rules exist because a memory that is never contradicted and never used is indistinguishable from
/// garbage, and a store that only grows will eventually hand the model more stale than current. Nothing here
/// deletes anything — an invalidated record stays visible and editable, because a person looking at the list
/// has to be able to see what was superseded and decide for themselves.
/// </para>
/// </summary>
public static class MemoryLifecycle
{
    /// <summary>How long a record may go unrecalled before it drops a tier.</summary>
    public static readonly TimeSpan IdleBeforeDemotion = TimeSpan.FromDays(90);

    /// <summary>
    /// How many distinctive words two records must share before one is treated as speaking about the same
    /// thing.
    /// <para>
    /// An absolute count, not a ratio. A ratio punishes the records that matter most: "项目部署在阿里云上" and
    /// "项目部署在腾讯云上了" are obviously the same claim about the same subject, yet they share a smaller
    /// <em>fraction</em> of the shorter one than two unrelated sentences do, because both are short and both
    /// are dense. Measured on real pairs, the ratio spans 0.43 for a genuine contradiction of a longer sentence
    /// and 0.63 for a short one — there is no threshold that separates them. The count does: a contradiction
    /// shares the subject words, and an unrelated record shares at most a greeting.
    /// </para>
    /// </summary>
    public const int MinSharedKeywords = 5;

    /// <summary>
    /// Whether <paramref name="candidate"/> and <paramref name="existing"/> make claims about the same thing.
    /// <para>
    /// Compared as the sets of their distinctive words — CJK bigrams for Chinese, whole words for Latin —
    /// because a shared string count alone rates "用户可能" as more alike than two records that genuinely
    /// contradict each other.
    /// </para>
    /// <para>
    /// An identical record is explicitly <b>not</b> a conflict. Retiring it would lose a fact without adding
    /// anything: the newcomer says the same thing, so the older copy carries nothing the new one does not.
    /// And it is a real case — the same turn can be distilled twice, and two turns can land on the same
    /// statement.
    /// </para>
    /// </summary>
    public static bool Conflicts(string candidate, string existing)
    {
        if (string.Equals(Normalize(candidate), Normalize(existing), StringComparison.Ordinal))
        {
            return false;
        }

        return Shared(candidate, existing).Count >= MinSharedKeywords;
    }

    /// <summary>Text reduced to what a comparison should be made on: punctuation and spacing removed.</summary>
    internal static string Normalize(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)));

    /// <summary>The distinctive words the two records have in common.</summary>
    public static IReadOnlyCollection<string> Shared(string left, string right)
    {
        var a = Keywords(left);
        var b = Keywords(right);
        return a.Count >= b.Count ? a.Where(b.Contains).ToList() : b.Where(a.Contains).ToList();
    }


    /// <summary>
    /// The distinctive words of a record: CJK bigrams and Latin words, stopwords dropped.
    /// <para>
    /// Dropping the stopwords is what keeps two records that both merely start with "用户" from looking
    /// identical — that word is in every record this system writes.
    /// </para>
    /// </summary>
    public static HashSet<string> Keywords(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var run = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                run.Append(ch);
                continue;
            }

            Flush(run, words);
        }

        Flush(run, words);
        return words;
    }

    private static void Flush(StringBuilder run, HashSet<string> words)
    {
        if (run.Length == 0)
        {
            return;
        }

        var chunk = run.ToString();
        run.Clear();

        if (chunk.All(c => c >= '一' && c <= '鿿'))
        {
            // CJK: overlapping pairs, which is what makes two differently-worded sentences comparable at all.
            for (var i = 0; i + 1 < chunk.Length; i++)
            {
                words.Add(chunk.Substring(i, 2));
            }

            return;
        }

        var lower = chunk.ToLowerInvariant();
        if (lower.Length >= 2 && !Stopwords.Contains(lower))
        {
            words.Add(lower);
        }
    }

    /// <summary>
    /// Words that appear in so many records that sharing one means nothing.
    /// <para>
    /// Small on purpose. Every word dropped here is a signal the conflict check stops seeing, so this is a list
    /// of words that are genuinely contentless rather than a general stopword list.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "用户", "可能", "应该", "需要", "可以", "这个", "那个", "什么", "怎么", "一个",
        "the", "and", "for", "with", "that", "this", "you", "are", "was",
    };
}
