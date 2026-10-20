using System.Text;

namespace Partner.Core.Memory;

/// <summary>
/// Keeps the store honest about two things: which record speaks for a topic, and which records have gone
/// quiet long enough to stop being trusted.
/// <para>
/// Nothing here deletes anything — an invalidated record stays visible and editable, so a person can see what
/// was superseded and decide.
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
    /// An absolute count, not a ratio: a ratio punishes short, dense records, and measured on real pairs the
    /// ratio ranges overlap between genuine contradictions and unrelated text. A contradiction shares the
    /// subject words; an unrelated record shares at most a greeting.
    /// </para>
    /// </summary>
    public const int MinSharedKeywords = 5;

    /// <summary>
    /// Whether <paramref name="candidate"/> and <paramref name="existing"/> make claims about the same thing.
    /// <para>
    /// Compared as sets of distinctive words — CJK bigrams for Chinese, whole words for Latin — because a raw
    /// shared-string count rates common fragments as more alike than a genuine contradiction.
    /// </para>
    /// <para>
    /// An identical record is explicitly <b>not</b> a conflict: the newcomer says the same thing, so retiring the
    /// older copy would lose nothing but would not gain anything either. The same turn can be distilled twice.
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

    /// <summary>
    /// The incoming record, analysed once, for comparing against many existing ones. <see cref="Conflicts(string, string)"/>
    /// splits both texts into keywords on every call; a write compares against up to a few hundred candidates and
    /// the incoming side never changes.
    /// </summary>
    public sealed class Incoming(string body)
    {
        private readonly string _normalized = Normalize(body);
        private readonly HashSet<string> _keywords = Keywords(body);

        public bool ConflictsWith(string existing)
        {
            if (string.Equals(_normalized, Normalize(existing), StringComparison.Ordinal))
            {
                return false;
            }

            var other = Keywords(existing);
            var shared = 0;
            foreach (var word in _keywords.Count <= other.Count ? _keywords : other)
            {
                if ((_keywords.Count <= other.Count ? other : _keywords).Contains(word))
                {
                    shared++;
                }
            }

            return shared >= MinSharedKeywords;
        }
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
    /// Dropping stopwords keeps two records that both merely start with the same common subject word from looking
    /// identical.
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
    /// Words that appear in so many records that sharing one means nothing. Every word dropped here is a signal
    /// the conflict check stops seeing, so this is a list of contentless words rather than a general stopword
    /// list.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "用户", "可能", "应该", "需要", "可以", "这个", "那个", "什么", "怎么", "一个",
        "the", "and", "for", "with", "that", "this", "you", "are", "was",
    };
}
