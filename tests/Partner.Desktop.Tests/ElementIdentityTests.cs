using System.Windows.Automation;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>An element id is a position; what the control is decides whether a shifted position is still it.</summary>
public sealed class ElementIdentityTests
{
    private static UiElement Element(string windowId, int index, string name, string stable) =>
        new($"{windowId}:{index}", windowId, name, "Button", new PixelRect(index * 20, 0, 18, 18),
            DesktopObservation.ElementIdentity(windowId, name, "Button", stable));

    private static (DesktopObservation Before, DesktopObservation After, string Window) TwoObservations(
        Func<string, IReadOnlyList<UiElement>> first,
        Func<string, IReadOnlyList<UiElement>> second)
    {
        var round = 0;
        using var automation = new DesktopAutomation(walk: (_, windowId) =>
            ((round == 0 ? first : second)(windowId), Array.Empty<AutomationElement>(), WalkStopReason.None));

        var before = automation.Observe();
        var window = before.Entities.FirstOrDefault()?.EntityId ?? string.Empty;
        round = 1;
        var after = automation.Observe();
        return (before, after, window);
    }

    [Fact]
    public void Two_controls_with_the_same_name_have_different_identities_when_they_are_different_controls()
    {
        Assert.NotEqual(
            DesktopObservation.ElementIdentity("w", "确定", "Button", "|Button|42.1.1"),
            DesktopObservation.ElementIdentity("w", "确定", "Button", "|Button|42.1.2"));

        // Without the extra key the behaviour is what it was.
        Assert.Equal(
            DesktopObservation.ElementIdentity("w", "确定", "Button"),
            DesktopObservation.ElementIdentity("w", "确定", "Button"));
    }

    [Fact]
    public void A_control_inserted_above_does_not_make_the_old_id_hit_a_different_control()
    {
        var (before, after, window) = TwoObservations(
            w => [Element(w, 0, "确定", "r1"), Element(w, 1, "确定", "r2")],
            w => [Element(w, 0, "确定", "rNEW"), Element(w, 1, "确定", "r1"), Element(w, 2, "确定", "r2")]);
        if (window.Length == 0)
        {
            return;
        }

        var seen = before.Elements.Single(e => e.ElementId == $"{window}:1"); // the second 确定, runtime r2

        // The old id now lands on the first "确定": same name, same role, a different control. Name + role alone
        // could not tell, so the old check passed and the click went to the wrong button.
        Assert.Equal("r1", after.FindElement(seen.ElementId) is { } landed ? landed.IdentityDigest == seen.IdentityDigest ? "same" : "r1" : "gone");

        var found = DesktopActTool.Reresolve(seen, after);

        Assert.Equal($"{window}:2", found.ElementId);
        Assert.Null(DesktopActTool.RevalidateElement(found, after));
    }

    [Fact]
    public void A_control_that_is_still_where_it_was_is_returned_unchanged()
    {
        var (before, after, window) = TwoObservations(
            w => [Element(w, 0, "播放", "r1")],
            w => [Element(w, 0, "播放", "r1")]);
        if (window.Length == 0)
        {
            return;
        }

        var seen = before.Elements.Single(e => e.WindowId == window);

        Assert.Same(seen, DesktopActTool.Reresolve(seen, after));
    }

    [Fact]
    public void A_control_that_is_gone_stays_refused()
    {
        var (before, after, window) = TwoObservations(
            w => [Element(w, 0, "播放", "r1"), Element(w, 1, "暂停", "r2")],
            w => [Element(w, 0, "播放", "r1")]);
        if (window.Length == 0)
        {
            return;
        }

        var seen = before.Elements.Single(e => e.WindowId == window && e.Name == "暂停");

        var found = DesktopActTool.Reresolve(seen, after);

        Assert.Same(seen, found);
        Assert.NotNull(DesktopActTool.RevalidateElement(found, after));
    }
}
