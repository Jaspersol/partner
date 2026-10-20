namespace Partner.Core.Sessions;

/// <summary>
/// The "facts" line of a structured summary, as separate statements.
/// <para>
/// The compactor asks the model for labelled lines (goal, decided, facts, open, in progress). The facts are the
/// part that stays true regardless of the conversation they came from, so they are worth keeping once the
/// conversation is folded away.
/// </para>
/// </summary>
public static class SummaryFacts
{
    private const int MinChars = 4;
    private const int MaxChars = 200;
    private const int MaxFacts = 8;

    private static readonly string[] Labels = { "Facts", "事实" };

    private static readonly char[] Separators = { '；', ';', '\n', '。' };

    /// <summary>The statements on the facts line, trimmed and bounded; empty when the summary has none.</summary>
    public static IReadOnlyList<string> From(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return [];
        }

        foreach (var line in summary.Split('\n'))
        {
            var trimmed = line.Trim();

            foreach (var label in Labels)
            {
                if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rest = trimmed[label.Length..].TrimStart();
                if (rest.Length == 0 || rest[0] is not (':' or '：'))
                {
                    continue;
                }

                return rest[1..]
                    .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(fact => fact.Length >= MinChars)
                    .Select(fact => fact.Length <= MaxChars ? fact : fact[..MaxChars])
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxFacts)
                    .ToList();
            }
        }

        return [];
    }
}
