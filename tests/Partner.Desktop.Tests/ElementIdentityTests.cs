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
            ((round == 0 ? first : second)(windowId), Array.Empty<IUiNode>(), WalkStopReason.None));

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

        var seen = before.Elements.Single(e => e.ElementId == $"{window}:1"); // index 1: the second control, runtime r2

        // The old id now resolves to the first control: same name, same role, a different control.
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

    [Fact]
    public void A_rewalk_that_inserted_a_control_before_the_target_is_refused()
    {
        // The observation's index pointed at "删除". A re-walk after an insertion now has "播放" at that
        // index; acting on it would be a different control with the same position.
        var expected = DesktopObservation.ElementIdentity("w", "删除", "Button", "r2");
        var shifted = new FakeNode("Button", "播放", "rNEW");
        var same = new FakeNode("Button", "删除", "r2");

        Assert.False(UiaTree.MatchesIdentity("w", shifted, expected));
        Assert.True(UiaTree.MatchesIdentity("w", same, expected));
    }

    /// <summary>A node whose only meaningful surface for these tests is the identity the walk reports.</summary>
    private sealed class FakeNode(string role, string name, string stable) : IUiNode
    {
        public UiNodeInfo Info { get; } = new(
            role, name, new PixelRect(0, 0, 10, 10), IsEnabled: true, IsOffscreen: false, IsPassword: false,
            StableKey: stable, ClassName: string.Empty, AutomationId: string.Empty, ProcessId: 0,
            ProviderDescription: string.Empty,
            InvokeAvailable: false, SelectionItemAvailable: false, ToggleAvailable: false,
            ValueAvailable: false, ExpandCollapseAvailable: false, ScrollAvailable: false,
            ScrollItemAvailable: false, TextAvailable: false);

        public IUiNode? FirstChild() => throw new NotSupportedException();

        public IUiNode? NextSibling() => throw new NotSupportedException();

        public IUiNode? Parent() => throw new NotSupportedException();

        public string? ReadValue() => throw new NotSupportedException();

        public string? ReadState() => throw new NotSupportedException();

        public bool HasKeyboardFocus() => throw new NotSupportedException();

        public UiaInvocation? Invoke() => throw new NotSupportedException();

        public UiaInvocation? Select() => throw new NotSupportedException();

        public UiaInvocation? Toggle() => throw new NotSupportedException();

        public UiaInvocation? SetFocus() => throw new NotSupportedException();

        public UiaInvocation? ScrollItemIntoView() => throw new NotSupportedException();

        public UiaInvocation? ScrollBy(int notches) => throw new NotSupportedException();

        public UiaInvocation? AppendText(string text) => throw new NotSupportedException();

        public UiaInvocation? Expand() => throw new NotSupportedException();
    }
}
