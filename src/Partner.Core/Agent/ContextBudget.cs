using Partner.Core.Memory;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

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

    /// <summary>Splits replayable history into complete turns, using item IDs when available and input boundaries for older logs.</summary>
    public static List<List<SessionViewItem>> GroupTurns(IReadOnlyList<SessionViewItem> items)
    {
        var groups = new List<List<SessionViewItem>>();
        string? groupId = null;
        foreach (var item in items)
        {
            var id = item.Meta is not null && item.Meta.TryGetValue("itemId", out var value) && !string.IsNullOrEmpty(value)
                ? value : null;
            var starts = groups.Count == 0
                || (id is not null && groupId is not null && id != groupId)
                || (item.Kind is SessionEventKind.User or SessionEventKind.Schedule
                    && (id is null || groupId is null));
            if (starts)
            {
                groups.Add(new List<SessionViewItem>());
                groupId = id;
            }
            else if (groupId is null && id is not null)
            {
                groupId = id;
            }
            groups[^1].Add(item);
        }
        return groups;
    }

    /// <summary>Fits a contiguous suffix of whole turns; an oversized newest turn is omitted in full.</summary>
    public static async ValueTask<List<SessionViewItem>> FitNewestGroupsAsync(
        IReadOnlyList<SessionViewItem> items,
        Func<SessionViewItem, CancellationToken, ValueTask<int>> cost,
        int budget,
        CancellationToken ct = default)
    {
        var groups = GroupTurns(items);
        var first = groups.Count;
        var used = 0;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var price = 0;
            foreach (var item in groups[i]) price += await cost(item, ct).ConfigureAwait(false);
            if (price > budget - used) break;
            used += price;
            first = i;
        }
        return groups.Skip(first).SelectMany(group => group).ToList();
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
