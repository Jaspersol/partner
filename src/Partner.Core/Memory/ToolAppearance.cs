using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Core.Memory;

/// <summary>
/// What to remember from a turn the model spent inside tools instead of prose.
/// <para>
/// Desktop work answers in tool results: the assistant text is short or empty, and the conclusions a later
/// turn needs (which path was taken, which window was acted on, that it worked) live in the
/// <c>ToolResult</c> events. The store kept missing all of it, because the distiller required "an answer".
/// </para>
/// <para>
/// Conservative by construction: only results that are both marked ok and written in the automation's
/// class of success — a window was acted on at its control centre, which is what the result text says when a
/// click landed where it was aimed (or rolled there, "snapped"). A refused call, an unverifiable one, and a
/// failed command read as prose the filter cannot vouch for, and stay out.
/// </para>
/// </summary>
public static class ToolAppearance
{
    /// <summary>Success markers the desktop act tool writes into an executed result.</summary>
    private static readonly string[] SuccessMarkers =
    {
        "已通过界面自动化执行",
        "已在后台执行",
        "注入成功",
        "已按控件中心点执行",
    };

    /// <summary>
    /// The body one tool-only exchange is worth remembering as, or null when nothing qualifies.
    /// <para>
    /// Takes the single most informative result rather than a concatenation: one act's line "LeftClick ·
    /// 注入成功 · 校验：画面已变化 · 目标 '播放' [button] @ 播放器" already carries the action, target, and
    /// outcome, and stacking several turns into one record would outrun the distiller's body cap.
    /// </para>
    /// </summary>
    /// <param name="items">The projected view; read for the ToolCall/ToolResult pair the turn produced.</param>
    /// <param name="afterSeq">Only results from this seq onward — the exchange this turn produced.</param>
    public static string? Summarize(IReadOnlyList<SessionViewItem> items, long afterSeq)
    {
        string? best = null;
        var bestMeta = 0;

        foreach (var item in items)
        {
            if (item.Kind != SessionEventKind.ToolResult || item.Seq < afterSeq)
            {
                continue;
            }

            if (item.Meta is null || item.Meta.GetValueOrDefault("ok") != "true")
            {
                continue;
            }

            var name = item.Meta.GetValueOrDefault("name");
            var body = item.Body ?? string.Empty;

            if (name != "desktop_act")
            {
                continue;
            }

            if (!SuccessMarkers.Any(marker => body.Contains(marker, StringComparison.Ordinal)))
            {
                continue;
            }

            if (body.Length <= bestMeta)
            {
                continue;
            }

            best = body;
            bestMeta = body.Length;
        }

        return best;
    }
}
