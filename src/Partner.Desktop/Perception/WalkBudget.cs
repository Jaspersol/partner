using System.Diagnostics;

namespace Partner.Desktop.Perception;

/// <summary>Why a UI Automation walk stopped, or <see cref="None"/> while it was still going.</summary>
public enum WalkStopReason
{
    /// <summary>The walk finished, or stopped because the tree ran out.</summary>
    None,

    /// <summary>Deeper than the controls worth finding.</summary>
    Depth,

    /// <summary>The window contributed as many elements as one window may.</summary>
    Produced,

    /// <summary>The window's tree is bigger than it is worth walking.</summary>
    Visited,

    /// <summary>The window took longer than one window may take.</summary>
    Time,
}

/// <summary>
/// What bounds one window's UI Automation walk.
/// <para>
/// The walk used to be bounded by depth and by how many elements it had <i>produced</i> — and the second of
/// those bounds nothing at all when the window has few interesting controls. A Chromium window is mostly
/// <c>Custom</c> elements that the walk skips, so the element count never reaches its cap and the walk keeps
/// descending through the entire tree, paying a cross-process RPC per node. The number that actually costs
/// time is the number of nodes <b>visited</b>, and that is what this counts.
/// </para>
/// <para>
/// The time budget is the other half of it: depth and element caps say nothing about a window whose provider
/// simply does not answer, and one such window is enough to hold the whole observation. It is deliberately
/// generous — it exists for a provider that hangs, not for a tree that is merely large, and the visited cap
/// is what handles the latter.
/// </para>
/// <para>
/// A record with a pure <see cref="Stop"/>, deliberately: the walk itself needs a real window, and a rule
/// that can only be checked by watching a stopwatch is a rule that drifts. Each field here is a number that
/// can be asserted without a desktop.
/// </para>
/// </summary>
public sealed record WalkBudget
{
    /// <summary>
    /// Nodes one window may visit. Roughly an order of magnitude above what a control-rich window needs, and
    /// far below the size of the trees this exists for: the cap is here for windows whose nodes are mostly
    /// elements the walk skips, not to trim real answers.
    /// </summary>
    public int MaxVisited { get; init; } = 800;

    /// <summary>Controls one window may contribute, so one app cannot fill the whole answer.</summary>
    public int MaxProduced { get; init; } = 40;

    /// <summary>How deep to walk. Deep trees are mostly layout panes; the controls live near the top.</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>
    /// How long one window may take.
    /// <para>
    /// Deliberately above the slowest window measured on a real desktop — 3.4 s for a window when this
    /// application's own WebView2 window was open — because a budget that cuts off a merely slow window turns
    /// a working one into a blind one, which is the opposite of what this is for. It bounds a provider that
    /// never answers, not one that is busy; the visited cap is what handles a tree that is merely large.
    /// </para>
    /// </summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMilliseconds(5000);

    public static WalkBudget Default { get; } = new();

    /// <summary>
    /// Whether the walk may take one more node, and why not when it may not.
    /// <para>
    /// Order matters only for which reason gets reported; every one of them stops the walk. Depth is checked
    /// first because it is the cheapest and the most specific, then the produced cap (the original bound,
    /// kept — it is what stops one app from filling the answer), then the two that bound work rather than
    /// output.
    /// </para>
    /// </summary>
    public WalkStopReason Stop(int depth, int produced, int visited, double elapsedMs)
    {
        if (depth > MaxDepth)
        {
            return WalkStopReason.Depth;
        }

        if (produced >= MaxProduced)
        {
            return WalkStopReason.Produced;
        }

        if (visited >= MaxVisited)
        {
            return WalkStopReason.Visited;
        }

        return elapsedMs >= TimeBudget.TotalMilliseconds ? WalkStopReason.Time : WalkStopReason.None;
    }

    /// <summary>Whether the walk may take one more node.</summary>
    public bool Allows(int depth, int produced, int visited, double elapsedMs) =>
        Stop(depth, produced, visited, elapsedMs) == WalkStopReason.None;
}

/// <summary>
/// The mutable half of a walk: what has been visited so far and whether it has already stopped.
/// <para>
/// Split out from <see cref="WalkBudget"/> so the rules stay a pure function that a test can pin, while the
/// recursion carries only counters. Once stopped it stays stopped: a walk that kept going after hitting its
/// budget would report a reason it then stopped honouring.
/// </para>
/// </summary>
internal sealed class WalkState(WalkBudget budget, Stopwatch clock)
{
    public WalkBudget Budget { get; } = budget;

    public int Visited { get; private set; }

    public WalkStopReason Stopped { get; private set; } = WalkStopReason.None;

    /// <summary>Counts one node about to be examined.</summary>
    public void Visit() => Visited++;

    /// <summary>Whether one more node may be examined — and remembers the reason when it may not.</summary>
    public bool Allows(int depth, int produced)
    {
        if (Stopped != WalkStopReason.None)
        {
            return false;
        }

        Stopped = Budget.Stop(depth, produced, Visited, Clock.Elapsed.TotalMilliseconds);
        return Stopped == WalkStopReason.None;
    }

    private Stopwatch Clock { get; } = clock;
}