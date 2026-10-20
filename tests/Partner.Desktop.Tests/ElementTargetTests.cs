using System.Windows.Automation;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>A control named by what it is called: one match is the control, anything else is a refusal that says why.</summary>
public sealed class ElementTargetTests
{
    private static UiElement Element(string windowId, int index, string role, string name) =>
        new($"{windowId}:{index}", windowId, name, role, new PixelRect(index * 10, 0, 10, 10), "d");

    private static DesktopObservation Observe(params (string Role, string Name)[] controls)
    {
        using var automation = new DesktopAutomation(walk: (_, windowId) =>
            (controls.Select((c, i) => Element(windowId, i, c.Role, c.Name)).ToList(),
                Array.Empty<AutomationElement>(),
                WalkStopReason.None));
        return automation.Observe(walkWindow: window => window.IsForeground) is { Elements.Count: > 0 } narrowed
            ? narrowed
            : automation.Observe();
    }

    private static string? WindowOf(DesktopObservation observation) => observation.Elements.FirstOrDefault()?.WindowId;

    [Fact]
    public void One_control_with_that_name_is_the_control()
    {
        var observation = Observe(("Button", "播放"), ("Button", "暂停"));

        var (element, error) = DesktopActTool.ResolveByName(
            observation, WindowOf(observation), new DesktopActTool.ElementTarget("播放", null));

        Assert.Null(error);
        Assert.Equal("播放", element!.Name);
    }

    [Fact]
    public void Two_controls_with_the_same_name_are_refused_and_listed_not_guessed_between()
    {
        var observation = Observe(("Button", "确定"), ("Button", "确定"), ("Edit", "名称"));

        var (element, error) = DesktopActTool.ResolveByName(
            observation, WindowOf(observation), new DesktopActTool.ElementTarget("确定", null));

        Assert.Null(element);
        Assert.Contains("不止一个", error!, StringComparison.Ordinal);
        Assert.Contains("elementId", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_tells_two_same_named_controls_apart()
    {
        var observation = Observe(("Button", "搜索"), ("Edit", "搜索"));

        var (element, error) = DesktopActTool.ResolveByName(
            observation, WindowOf(observation), new DesktopActTool.ElementTarget("搜索", "Edit"));

        Assert.Null(error);
        Assert.Equal("Edit", element!.Role);
    }

    [Fact]
    public void A_name_that_matches_nothing_says_so_and_a_partial_name_is_a_fallback()
    {
        var observation = Observe(("Button", "播放列表"));

        var none = DesktopActTool.ResolveByName(
            observation, WindowOf(observation), new DesktopActTool.ElementTarget("下载", null));
        Assert.Contains("没有叫", none.Error!, StringComparison.Ordinal);

        var partial = DesktopActTool.ResolveByName(
            observation, WindowOf(observation), new DesktopActTool.ElementTarget("播放", null));
        Assert.Equal("播放列表", partial.Element!.Name);
    }

    [Fact]
    public void A_target_without_a_name_is_refused()
    {
        var observation = Observe(("Button", "播放"));

        var (element, error) = DesktopActTool.ResolveByName(
            observation, WindowOf(observation), new DesktopActTool.ElementTarget(null, "Button"));

        Assert.Null(element);
        Assert.Contains("需要 name", error!, StringComparison.Ordinal);
    }
}
