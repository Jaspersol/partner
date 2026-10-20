using System.Text;

namespace Partner.Core.Memory;

/// <summary>
/// CJK half of the coarse filter.
/// <para>
/// FTS5's <c>unicode61</c> tokenizer has no word segmentation for Chinese: a whole sentence becomes one token, so
/// a question worded differently from the record matches nothing. Text is therefore cut into overlapping
/// character bigrams on the way in, and the query is cut the same way on the way out, so the two can share
/// bigrams and the index can rank them.
/// </para>
/// </summary>
internal static class CjkText
{
    /// <summary>CJK ideographs and kana. Both are written without spaces.</summary>
    public static bool IsCjk(char ch) => ch is >= '一' and <= '鿿' || ch is >= '぀' and <= 'ヿ';

    /// <summary>
    /// Overlapping character pairs.
    /// <para>
    /// A run of one character yields nothing: a bigram needs a neighbour, and indexing single characters would
    /// make the shortest queries match most of the store.
    /// </para>
    /// </summary>
    public static IEnumerable<string> Bigrams(string text)
    {
        var run = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                run.Append(ch);
                continue;
            }

            foreach (var bigram in Flush(run))
            {
                yield return bigram;
            }
        }

        foreach (var bigram in Flush(run))
        {
            yield return bigram;
        }

        static IEnumerable<string> Flush(StringBuilder run)
        {
            var flushed = new List<string>(Math.Max(0, run.Length - 1));
            if (run.Length >= 2)
            {
                for (var i = 0; i + 1 < run.Length; i++)
                {
                    flushed.Add(run.ToString(i, 2));
                }
            }

            run.Clear();
            return flushed;
        }
    }

    /// <summary>
    /// What is stored in <c>memories.body_cjk</c>: this text's distinct bigrams, space separated.
    /// <para>
    /// Spaces make them separate tokens to <c>unicode61</c>. This only decides which records are candidates; the
    /// scorer decides relevance.
    /// </para>
    /// </summary>
    public static string Index(string text) =>
        string.Join(' ', Bigrams(text).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// The FTS5 expression that finds records sharing a bigram with <paramref name="query"/>, or null when the
    /// query has no CJK bigram to look for.
    /// <para>
    /// Quoted and OR-ed. Every character comes from <see cref="Bigrams"/> and is a CJK codepoint, so none can
    /// carry FTS5 syntax; that is why concatenation is safe here while
    /// <see cref="MemoryStore.BuildMatchExpression"/> (which takes arbitrary text) has to escape.
    /// </para>
    /// </summary>
    public static string? MatchExpression(string query)
    {
        var tokens = Bigrams(query).Distinct(StringComparer.Ordinal).ToList();
        return tokens.Count == 0 ? null : string.Join(" OR ", tokens.Select(t => "\"" + t + "\""));
    }
}
