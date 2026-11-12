using System.Runtime.InteropServices;
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
    [InlineData(WalkStopReason.Depth, true)]
    [InlineData(WalkStopReason.Produced, true)]
    [InlineData(WalkStopReason.Visited, true)]
    [InlineData(WalkStopReason.Time, true)]
    [InlineData(WalkStopReason.Fault, true)]
    [InlineData(WalkStopReason.Refused, true)]
    [InlineData(WalkStopReason.Unresponsive, true)]
    [InlineData(WalkStopReason.Gone, true)]
    public void Every_reason_other_than_none_means_the_list_may_be_incomplete(
        WalkStopReason stopped,
        bool expected)
    {
        Assert.Equal(expected, DesktopAutomation.CutOff(stopped) is not null);
    }

    [Fact]
    public void A_fault_is_not_reported_as_an_ordinary_bound()
    {
        // A provider that failed and a tree that simply ran out look identical if both are dropped. The
        // reason travels with the window; the tool turns each one into a different next step.
        Assert.Equal(WalkStopReason.Fault, DesktopAutomation.CutOff(WalkStopReason.Fault));
        Assert.Equal(WalkStopReason.Unresponsive, DesktopAutomation.CutOff(WalkStopReason.Unresponsive));
        Assert.Null(DesktopAutomation.CutOff(WalkStopReason.None));
    }

    [Fact]
    public void A_hung_provider_is_classified_as_unresponsive_not_as_a_broken_tree()
    {
        // The HRESULTs are the only evidence a COM failure carries; mapping them all to "fault" would make
        // "wait and retry" and "this window will never answer" look the same.
        Assert.Equal(
            WalkStopReason.Unresponsive,
            UiaErrors.Classify(new COMException("timeout", unchecked((int)0x8001011F))));
        Assert.Equal(
            WalkStopReason.Refused,
            UiaErrors.Classify(new UnauthorizedAccessException()));
        Assert.Equal(
            WalkStopReason.Gone,
            UiaErrors.Classify(new COMException("gone", unchecked((int)0x80040201))));
        Assert.Equal(
            WalkStopReason.Fault,
            UiaErrors.Classify(new InvalidOperationException()));
    }

    [Fact]
    public void A_walk_that_throws_degrades_to_incomplete_instead_of_failing_the_observation()
    {
        using var automation = new DesktopAutomation(walk: (_, _) => throw new TimeoutException());

        var observation = automation.Observe();

        if (observation.Entities.Count == 0)
        {
            return; // no interactive desktop in this environment
        }

        Assert.All(observation.Entities, entity =>
        {
            Assert.True(entity.ElementsTruncated);
            Assert.Equal(WalkStopReason.Unresponsive, entity.ElementsTruncation);
        });
    }

    [Fact]
    public void A_faulting_walk_is_reported_on_the_window()
    {
        using var automation = new DesktopAutomation(walk: (_, _) =>
            (Array.Empty<UiElement>(), Array.Empty<IUiNode>(), WalkStopReason.Fault));

        var observation = automation.Observe();

        if (observation.Entities.Count == 0)
        {
            return; // no interactive desktop in this environment
        }

        Assert.All(observation.Entities, entity =>
        {
            Assert.True(entity.ElementsTruncated);
            Assert.Equal(WalkStopReason.Fault, entity.ElementsTruncation);
        });
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