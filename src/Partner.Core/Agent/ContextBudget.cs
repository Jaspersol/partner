using Partner.Core.Memory;

namespace Partner.Core.Agent;

/// <summary>
/// How much history fits. The rule lives here because two places must agree on it: the assembler decides
/// what to send, and the compactor decides what to fold — a disagreement would either send more than the
/// window allows or fold more than necessary.
/// </summary>
public static class ContextBudget
{
    /// <summary>Per-message overhead the counters do not see (role markers, separators).</summary>
    public const int MessageOverheadTokens = 4;

    /// <summary>
    /// What one attached image is charged against the window. A flat, conservative number: a vision model
    /// prices an image by its own tiling, which this layer cannot know, and charging nothing would let a
    /// prompt look like it fits while the image alone fills it.
    /// </summary>
    public const int ImageTokens = 1024;

    /// <summary>
    /// Keeps the newest contiguous suffix that fits <paramref name="budget"/>. A suffix rather than a
    /// selection: dropping a turn from the middle would leave the conversation answering something that is no
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
            kept.Insert(0, items[i]);
        }

        return kept;
    }

    /// <summary>Cost of one piece of text as a message: its tokens plus the framing overhead.</summary>
    public static async ValueTask<int> CostAsync(ITokenCounter counter, string? text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return await counter.CountAsync(text ?? string.Empty, ct).ConfigureAwait(false) + MessageOverheadTokens;
    }

    /// <summary>
    /// The room a prompt actually has: the context window minus what the completion may occupy. Sending a
    /// prompt that leaves no room for the answer is how a model ends up truncating mid-sentence.
    /// </summary>
    public static int PromptBudget(int contextTokens, int reserveForOutput)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);
        return Math.Max(1, contextTokens - Math.Max(0, reserveForOutput));
    }
}
