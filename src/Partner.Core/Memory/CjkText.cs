using System.Text;

namespace Partner.Core.Memory;

/// <summary>
/// The CJK half of the coarse filter (todo M1.1).
/// <para>
/// FTS5's <c>unicode61</c> tokenizer has no word segmentation for Chinese: a whole sentence becomes one token, so
/// a question worded differently from the record matches nothing — 我不太喜欢被打断 and 用户不喜欢被反复确认 share
/// no token at all. The tokenizer cannot be swapped underneath an existing store, so the text is instead cut into
/// overlapping character bigrams on the way in, and the query is cut the same way on the way out. What the two
/// then share is 喜欢 and 欢被, and that overlap is what the index can rank.
/// </para>
/// <para>
/// A real segmenter would be better and is a separate decision; this is the cheapest thing that makes the two
/// sentences intersect, and it is the same rule the previous full-scan implementation used — only now the
/// bigrams live in an index instead of in a LIKE pattern over every row.
/// </para>
/// </summary>
internal static class CjkText
{
    public static bool ContainsCjk(string text) => text.Any(IsCjk);

    /// <summary>
    /// CJK ideographs (一..鿿) and kana (぀..ヿ). Both are written without spaces, which is the whole problem.
    /// </summary>
    public static bool IsCjk(char ch) => ch is >= '一' and <= '鿿' || ch is >= '぀' and <= 'ヿ';

    /// <summary>
    /// Overlapping character pairs — what lets two differently-worded sentences intersect at all.
    /// <para>
    /// Runs of one character yield nothing: a bigram needs a neighbour, and the alternative (indexing every single
    /// character) makes the shortest queries match most of the store.
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
    /// What is stored in <c>memories.body_cjk</c>: this text's bigrams, space separated, each distinct one once.
    /// <para>
    /// Spaces are what makes them separate tokens to <c>unicode61</c>, and dropping repeats keeps a long record's
    /// index entry from growing with every occurrence of the same pair — term frequency is not what ranks here,
    /// the scorer decides relevance and this only decides who is in the running.
    /// </para>
    /// </summary>
    public static string Index(string text) =>
        string.Join(' ', Bigrams(text).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// The FTS5 expression that finds records sharing a bigram with <paramref name="query"/>, or null when the
    /// query has no CJK bigram to look for.
    /// <para>
    /// Quoted and OR-ed. The tokens come from <see cref="Bigrams"/>, so every character in them is a CJK
    /// codepoint — none of them can carry FTS5 syntax, which is why this one is built by concatenation while
    /// <see cref="MemoryStore.BuildMatchExpression"/> (which takes arbitrary text) has to escape.
    /// </para>
    /// </summary>
    public static string? MatchExpression(string query)
    {
        var tokens = Bigrams(query).Distinct(StringComparer.Ordinal).ToList();
        return tokens.Count == 0 ? null : string.Join(" OR ", tokens.Select(t => "\"" + t + "\""));
    }
}
