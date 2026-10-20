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
/// What bounds one window's UI Automation walk: nodes visited, controls produced, depth and elapsed time. A
/// produced-elements cap alone bounds nothing when a window has few interesting controls; a Chromium window is
/// mostly <c>Custom</c> elements the walk skips, so it would keep descending the whole tree and paying a
/// cross-process RPC per node. The time budget bounds a provider that never answers; the visited cap bounds a
/// tree that is merely large.
/// <para>
/// A record with a pure <see cref="Stop"/> so the rule can be asserted without a real window.
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
    /// How long one window may take. Deliberately generous: it bounds a provider that never answers, not one
    /// that is busy, and cutting off a merely slow window turns a working one into a blind one. The visited cap
    /// handles a tree that is merely large.
    /// </summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMilliseconds(5000);

    public static WalkBudget Default { get; } = new();

    /// <summary>
    /// Whether the walk may take one more node, and why not when it may not. Order matters only for which
    /// reason gets reported; every check stops the walk. Depth is first because it is cheapest and most
    /// specific, then the produced cap, then the two bounds on work rather than output.
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
/// The mutable half of a walk: what has been visited so far and whether it has already stopped. Split from
/// <see cref="WalkBudget"/> so the rules stay pure, while the recursion carries only counters. Once stopped it
/// stays stopped.
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