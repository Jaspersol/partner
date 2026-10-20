using System.Windows.Automation;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>The window tool refuses before it touches anything it cannot vouch for.</summary>
public sealed class DesktopWindowToolTests
{
    private static DesktopAutomation Automation() => new(walk: (_, _) =>
        (Array.Empty<UiElement>(), Array.Empty<AutomationElement>(), WalkStopReason.None));

    private static DesktopWindowTool Tool(DesktopAutomation automation, params string[] allowed) =>
        new(automation, new DesktopToolPolicy(allowed));

    [Fact]
    public async Task A_window_nobody_observed_is_refused()
    {
        using var automation = Automation();

        var result = await Tool(automation).InvokeAsync("""{"entityId":"abc","action":"maximize"}""");

        Assert.False(result.Ok);
        Assert.Contains("desktop_observe", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_arguments_and_unknown_actions_are_refused_with_what_is_valid()
    {
        using var automation = Automation();
        var observed = automation.Observe();
        if (observed.Entities.Count == 0)
        {
            return;
        }

        var window = observed.Entities[0].EntityId;
        var tool = Tool(automation);

        Assert.False((await tool.InvokeAsync("{}")).Ok);

        var unknown = await tool.InvokeAsync($$"""{"entityId":"{{window}}","action":"explode"}""");
        Assert.False(unknown.Ok);
        Assert.Contains("restore / maximize / minimize / move", unknown.Content, StringComparison.Ordinal);

        var noPlace = await tool.InvokeAsync($$"""{"entityId":"{{window}}","action":"move"}""");
        Assert.False(noPlace.Ok);
        Assert.Contains("x 和 y", noPlace.Content, StringComparison.Ordinal);

        var tiny = await tool.InvokeAsync($$"""{"entityId":"{{window}}","action":"move","x":0,"y":0,"width":10,"height":10}""");
        Assert.False(tiny.Ok);
        Assert.Contains("太小", tiny.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_outside_the_allow_list_is_refused()
    {
        using var automation = Automation();
        var observed = automation.Observe();
        if (observed.Entities.Count == 0)
        {
            return;
        }

        var window = observed.Entities[0].EntityId;

        var result = await Tool(automation, "只允许这个标题的窗口").InvokeAsync($$"""{"entityId":"{{window}}","action":"minimize"}""");

        Assert.False(result.Ok);
        Assert.Contains("允许列表", result.Content, StringComparison.Ordinal);
    }
}
