using Partner.Core.Memory;

namespace Partner.Core.Agent;

/// <summary>
/// How much history fits. The assembler and the compactor must both use this rule.
/// </summary>
public static class ContextBudget
{
    /// <summary>Per-message overhead the counters do not see (role markers, separators).</summary>
    public const int MessageOverheadTokens = 4;

    /// <summary>
    /// Flat, conservative charge per attached image: a vision model prices an image by its own tiling, which this
    /// layer cannot know.
    /// </summary>
    public const int ImageTokens = 1024;

    /// <summary>
    /// Keeps the newest contiguous suffix that fits <paramref name="budget"/>. A suffix rather than a
    /// selection: dropping a turn from the middle would leave the conversation answering something no
    /// longer in context.
    /// </summary>
    public static async ValueTask<List<T>> FitNewestFirstAsync<T>(
        IReadOnlyList<T> items,
        Func<T, CancellationToken, ValueTask<int>> cost,
        int budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(cost);

        var kept = new List<T>();

        if (budget <= 0)
        {
            return kept;
        }

        var used = 0;

        for (var i = items.Count - 1; i >= 0; i--)
        {
            var price = await cost(items[i], ct).ConfigureAwait(false);

            if (used + price > budget)
            {
                break;
            }

            used += price;
            kept.Add(items[i]);
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>Cost of one piece of text as a message: its tokens plus the framing overhead.</summary>
    public static async ValueTask<int> CostAsync(ITokenCounter counter, string? text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return await counter.CountAsync(text ?? string.Empty, ct).ConfigureAwait(false) + MessageOverheadTokens;
    }

    /// <summary>
    /// The context window minus what the completion may occupy. Leaving no room for the answer truncates it.
    /// </summary>
    public static int PromptBudget(int contextTokens, int reserveForOutput)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);
        return Math.Max(1, contextTokens - Math.Max(0, reserveForOutput));
    }
}
