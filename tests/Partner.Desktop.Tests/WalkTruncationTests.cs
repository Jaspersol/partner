using Partner.Desktop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// Saying out loud that a window's control list may be incomplete.
/// <para>
/// The walk is bounded, and on a Chromium-shaped window those bounds are the difference between milliseconds
/// and tens of seconds. But a walk that stopped early has not seen the whole window, and handing back its
/// partial list as if it were the whole one is how a control that exists goes missing with nothing in the
/// answer to explain it.
/// </para>
/// </summary>
public sealed class WalkTruncationTests
{
    [Theory]
    [InlineData(WalkStopReason.None, false)]
    [InlineData(WalkStopReason.Depth, false)]
    [InlineData(WalkStopReason.Produced, false)]
    [InlineData(WalkStopReason.Visited, true)]
    [InlineData(WalkStopReason.Time, true)]
    public void Only_running_out_of_budget_counts_as_may_have_missed_something(
        WalkStopReason stopped,
        bool expected)
    {
        Assert.Equal(expected, DesktopAutomation.CutOff(stopped) is not null);
    }

    [Fact]
    public void Depth_and_the_control_cap_are_ordinary_enough_not_to_be_reported()
    {
        // Every deep tree stops at the depth limit and every control-rich window stops at the cap. Warning on
        // those would put a caveat on almost every window and teach the model to ignore the caveat.
        Assert.Null(DesktopAutomation.CutOff(WalkStopReason.Depth));
        Assert.Null(DesktopAutomation.CutOff(WalkStopReason.Produced));
    }
}

/// <summary>
/// Narrowing what an observation hands back.
/// <para>
/// A nine-window desktop arrives as nine windows' worth of controls on every call, and the model acts on one
/// of them. The filter is the difference, and its failure mode is the thing worth testing: a filter that
/// matches nothing must not read as an empty desktop.
/// </para>
/// </summary>
public sealed class ObserveFilterTests
{
    private static UiEntity Window(string entityId, string label) => new()
    {
        EntityId = entityId,
        Generation = "g",
        Role = "window",
        Label = label,
        Rect = new PixelRect(0, 0, 100, 100),
        Confidence = 1,
        IdentityDigest = "i",
        LayoutDigest = "l",
        ProcessId = 1,
        ObservedAtMs = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void No_filter_matches_every_window()
    {
        var filter = DesktopObserveTool.DesktopObserveFilter.Parse("{}");

        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches(Window("a", "记事本")));
    }

    [Fact]
    public void A_window_can_be_named_by_its_id()
    {
        var filter = DesktopObserveTool.DesktopObserveFilter.Parse("""{"entityId":"b"}""");

        Assert.False(filter.IsEmpty);
        Assert.False(filter.Matches(Window("a", "记事本")));
        Assert.True(filter.Matches(Window("b", "计算器")));
    }

    [Theory]
    [InlineData("云音乐", "网易云音乐", true)]
    [InlineData("cloudmusic", "网易云音乐", false)]
    [InlineData("记事", "记事本", true)]
    [InlineData("记事", "计算器", false)]
    public void A_window_can_be_named_by_part_of_its_title(string wanted, string label, bool expected)
    {
        var filter = DesktopObserveTool.DesktopObserveFilter.Parse($$"""{"titleContains":"{{wanted}}"}""");

        Assert.Equal(expected, filter.Matches(Window("a", label)));
    }

    [Fact]
    public void Both_filters_have_to_pass()
    {
        var filter = DesktopObserveTool.DesktopObserveFilter.Parse("""{"entityId":"b","titleContains":"计算器"}""");

        Assert.True(filter.Matches(Window("b", "计算器")));
        Assert.False(filter.Matches(Window("b", "记事本")));
        Assert.False(filter.Matches(Window("c", "计算器")));
    }

    [Fact]
    public void A_malformed_call_widens_rather_than_narrows()
    {
        // Guessing a filter would hide windows the model asked to see — including the one it was about to act
        // on. A broken argument must cost visibility, never silently remove it.
        var filter = DesktopObserveTool.DesktopObserveFilter.Parse("not json at all");

        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches(Window("a", "记事本")));
    }

    [Fact]
    public void The_refusal_says_what_was_asked_for()
    {
        var filter = DesktopObserveTool.DesktopObserveFilter.Parse("""{"titleContains":"云音乐"}""");

        Assert.Contains("云音乐", filter.Describe(), StringComparison.Ordinal);
    }
}