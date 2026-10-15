using System.Windows.Automation;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>
/// What an observation's shared state has to survive: a second observation running at the same time.
/// <para>
/// The step budget allows parallel tool calls, so this is not hypothetical — and the failure it produced was
/// invisible: two observations mutating one dictionary of truncation reasons, so an entity could carry the
/// count belonging to somebody else's walk, and a half-built dictionary could be read by the other call.
/// </para>
/// <para>
/// Driven through the walk seam rather than against the real desktop: the interleaving is the subject, and the
/// real one cannot be made to happen on demand.
/// </para>
/// </summary>
public sealed class ObservationConcurrencyTests
{
    /// <summary>
    /// Set on the thread that starts the slow observation, so the seam can tell the two calls apart. Flows into
    /// the <see cref="Task.Run"/> that starts it and is cleared before the fast one is issued.
    /// </summary>
    private static readonly AsyncLocal<bool> SlowWalk = new();

    private static UiElement Element(string windowId, int index) => new(
        $"{windowId}:{index}",
        windowId,
        $"control {index}",
        "Button",
        new PixelRect(0, 0, 10, 10),
        "digest");

    private static class SlowWalkGate
    {
        public static readonly TaskCompletionSource Entered = new();
        public static readonly TaskCompletionSource Release = new();
    }

    /// <summary>
    /// A targeted observation and a full one in flight at once. The targeted call re-walks one window and
    /// carries the rest forward from the snapshot it started with; the full call walks everything and is cut
    /// off. What the targeted answer says about the windows it did <i>not</i> walk has to come from its own
    /// starting snapshot, not from the walk that happened while it was parked.
    /// <para>
    /// This is the interleaving that the shared-dictionary version loses. It parked the targeted call inside
    /// its walk, after the seed and before the answer was built; the full walk then cleared and refilled the
    /// one dictionary both of them held, so the targeted answer read the other call's reasons back out for
    /// windows it never looked at. Nothing threw, and nothing was missing — the reasons were simply somebody
    /// else's.
    /// </para>
    /// <para>
    /// The park is placed deliberately: <c>_walk</c> is called after the carry-forward seed and before any of
    /// this call's own entries are written, so the window between them is the whole subject. A park placed
    /// anywhere else proves nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_targeted_observation_keeps_its_own_snapshot_of_the_windows_it_did_not_walk()
    {
        var cutOff = false;
        using var automation = new DesktopAutomation(walk: (handle, windowId) =>
        {
            if (SlowWalk.Value)
            {
                // The targeted call, parked inside its one walk. Its own reason is decided here, so the
                // full walk's later choice cannot leak into it.
                SlowWalkGate.Entered.TrySetResult();
                SlowWalkGate.Release.Task.GetAwaiter().GetResult();
                return (new[] { Element(windowId, 0) }, Array.Empty<AutomationElement>(), WalkStopReason.None);
            }

            return (new[] { Element(windowId, 0) }, Array.Empty<AutomationElement>(),
                cutOff ? WalkStopReason.Visited : WalkStopReason.None);
        });

        // A clean full walk: this is the snapshot the targeted call carries the other windows forward from.
        var clean = automation.Observe();
        var target = clean.Entities[0].EntityId;
        var others = clean.Entities.Where(entity => entity.EntityId != target).ToList();
        Assert.NotEmpty(others);

        SlowWalk.Value = true;
        var targeted = Task.Run(() => automation.Observe(elementsFor: target));
        SlowWalk.Value = false;

        await SlowWalkGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The full walk runs to completion while the targeted one is parked — and it is the one cut off.
        cutOff = true;
        var cut = automation.Observe();
        Assert.All(cut.Entities, entity => Assert.True(entity.ElementsTruncated));

        SlowWalkGate.Release.SetResult();
        var result = await targeted;

        // The window that was re-walked is clean, and the windows that were not keep the reasons they were
        // published with — the clean walk's, not the concurrent cut-off walk's.
        Assert.False(result.Find(target)!.ElementsTruncated);
        Assert.All(
            result.Entities.Where(entity => entity.EntityId != target),
            entity => Assert.False(entity.ElementsTruncated));
    }

    /// <summary>
    /// A targeted observation carries the other windows forward untouched. The published state has to be
    /// replaced as a unit for that to hold: a walk that rebuilt the dictionaries in place would leave a
    /// concurrent call reading a half-populated one.
    /// </summary>
    [Fact]
    public void A_targeted_observation_publishes_the_other_windows_unchanged()
    {
        // Every window of the first walk is cut off; the second walk is clean.
        var cutOff = true;
        using var automation = new DesktopAutomation(walk: (handle, windowId) =>
            (new[] { Element(windowId, 0) }, Array.Empty<AutomationElement>(),
                cutOff ? WalkStopReason.Visited : WalkStopReason.None));

        var full = automation.Observe();
        var target = full.Entities[0].EntityId;
        var others = full.Entities.Where(entity => entity.EntityId != target).ToList();
        Assert.All(others, entity => Assert.True(entity.ElementsTruncated));

        cutOff = false;
        var targeted = automation.Observe(elementsFor: target);

        // The pair travels together: the window that was re-walked is now clean, and the ones that were not keep
        // the reason they were published with — not the freshly walked one's.
        Assert.False(targeted.Find(target)!.ElementsTruncated);
        Assert.All(
            targeted.Entities.Where(entity => entity.EntityId != target),
            entity => Assert.True(entity.ElementsTruncated));
    }
}
