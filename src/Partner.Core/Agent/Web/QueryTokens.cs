namespace Partner.Core.Agent.Web;

/// <summary>
/// The words a query is really made of, for the one question the decoy check asks: does this batch mention what
/// was asked for at all?
/// <para>
/// Chinese is not split on spaces, so a query like 北京天气 is one "word" to any whitespace splitter and would
/// never match anything. CJK text is therefore tokenized into single characters and adjacent pairs, which is the
/// cheap version of the segmentation the whole check would otherwise need — and it is enough for deciding whether
/// a page is about the subject, which is all this is for.
/// </para>
/// </summary>
public static class QueryTokens
{
    /// <summary>Single letters are not evidence. Two is the shortest token worth matching on.</summary>
    private const int MinLatinLength = 2;

    public static IReadOnlyCollection<string> Of(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var latin = new System.Text.StringBuilder();

        void FlushLatin()
        {
            if (latin.Length >= MinLatinLength)
            {
                tokens.Add(latin.ToString().ToLowerInvariant());
            }

            latin.Clear();
        }

        var previousCjk = '\0';

        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                FlushLatin();
                tokens.Add(character.ToString());

                if (previousCjk != '\0')
                {
                    tokens.Add(string.Concat(previousCjk, character));
                }

                previousCjk = character;
                continue;
            }

            previousCjk = '\0';

            if (char.IsLetterOrDigit(character))
            {
                latin.Append(character);
            }
            else
            {
                FlushLatin();
            }
        }

        FlushLatin();

        // A one-character Chinese query ("热") has no useful token set. Rather than judge every batch a decoy on
        // no evidence, the caller is told there is nothing to match on.
        return tokens;
    }

    /// <summary>
    /// What share of a batch mentions the query at all. This is the decoy signal: a healthy batch is highly
    /// consistent — nearly every result echoes the subject — and a batch served to a suspicious client is not
    /// about the subject at all.
    /// </summary>
    public static double EchoRatio(IEnumerable<SearchHit> hits, IReadOnlyCollection<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.Count == 0)
        {
            return 1;
        }

        var total = 0;
        var echoed = 0;

        foreach (var hit in hits)
        {
            total++;
            var haystack = string.Concat(hit.Title, "\n", hit.Snippet).ToLowerInvariant();

            if (tokens.Any(token => haystack.Contains(token, StringComparison.Ordinal)))
            {
                echoed++;
            }
        }

        return total == 0 ? 0 : (double)echoed / total;
    }

    private static bool IsCjk(char character) => character
        is >= '一' and <= '鿿'
        or >= '㐀' and <= '䶿'
        or >= '぀' and <= 'ヿ'
        or >= '가' and <= '힯'
        or >= '豈' and <= '﫿';
}
