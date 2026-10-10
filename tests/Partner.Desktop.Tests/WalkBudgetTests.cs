using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>
/// What bounds one UI Automation walk.
/// <para>
/// These are the rules the walk itself cannot be asked about without a real desktop — and the walk is exactly
/// where the time goes, so the rules have to be pinable on their own. The bug they exist for: the walk was
/// bounded by elements <i>produced</i>, which bounds nothing in a window made of controls it skips, so a
/// Chromium window was walked to its last node at a cross-process RPC apiece.
/// </para>
/// </summary>
public sealed class WalkBudgetTests
{
    private static WalkStopReason Stop(
        WalkBudget budget,
        int depth = 0,
        int produced = 0,
        int visited = 0,
        double elapsedMs = 0) => budget.Stop(depth, produced, visited, elapsedMs);

    [Fact]
    public void A_window_with_nothing_wrong_is_still_walked()
    {
        Assert.Equal(WalkStopReason.None, Stop(WalkBudget.Default));
        Assert.True(WalkBudget.Default.Allows(depth: 0, produced: 0, visited: 0, elapsedMs: 0));
    }

    [Fact]
    public void Depth_beyond_the_limit_stops_the_walk()
    {
        Assert.Equal(WalkStopReason.None, Stop(WalkBudget.Default, depth: WalkBudget.Default.MaxDepth));
        Assert.Equal(WalkStopReason.Depth, Stop(WalkBudget.Default, depth: WalkBudget.Default.MaxDepth + 1));
    }

    [Fact]
    public void The_element_cap_still_bounds_what_one_window_contributes()
    {
        Assert.Equal(WalkStopReason.None, Stop(WalkBudget.Default, produced: 39));
        Assert.Equal(WalkStopReason.Produced, Stop(WalkBudget.Default, produced: 40));
    }

    [Fact]
    public void A_window_of_skipped_controls_is_bounded_by_nodes_visited_not_by_elements_produced()
    {
        // The whole point: producing nothing, forever. Only the visited cap can stop this, and the old
        // produced-only condition never reached its limit here — which is why a Chromium window cost seconds.
        for (var visited = 0; visited < WalkBudget.Default.MaxVisited; visited++)
        {
            Assert.Equal(WalkStopReason.None, Stop(WalkBudget.Default, produced: 0, visited: visited));
        }

        Assert.Equal(
            WalkStopReason.Visited,
            Stop(WalkBudget.Default, produced: 0, visited: WalkBudget.Default.MaxVisited));
    }

    [Fact]
    public void The_visited_cap_is_well_above_the_elements_a_useful_window_has()
    {
        // Otherwise the new bound would silently truncate real answers to make the pathological case cheaper.
        Assert.True(
            WalkBudget.Default.MaxVisited >= WalkBudget.Default.MaxProduced * 4,
            $"a window that fills the element cap must not be near the visit cap");
    }

    [Fact]
    public void A_window_that_never_answers_is_stopped_by_the_time_budget()
    {
        var budget = WalkBudget.Default;

        Assert.Equal(WalkStopReason.None, Stop(budget, elapsedMs: budget.TimeBudget.TotalMilliseconds - 1));
        Assert.Equal(WalkStopReason.Time, Stop(budget, elapsedMs: budget.TimeBudget.TotalMilliseconds));
        Assert.Equal(WalkStopReason.Time, Stop(budget, elapsedMs: budget.TimeBudget.TotalMilliseconds * 10));
    }

    [Fact]
    public void The_time_budget_is_generous_enough_for_a_slow_provider_to_publish_its_tree()
    {
        // Chromium needs a moment to publish, and a window on a real desktop was measured at 3.4s when this
        // application's own WebView2 window was open. A budget that cut it off would trade a slow window for
        // a blind one, which is the opposite of what this is for.
        Assert.True(
            WalkBudget.Default.TimeBudget >= TimeSpan.FromSeconds(4),
            $"{WalkBudget.Default.TimeBudget} is too tight to let a slow app publish its tree");
    }

    [Fact]
    public void Which_reason_is_reported_does_not_change_whether_the_walk_stops()
    {
        // Every reason stops the walk; the order only decides what the log says.
        var budget = WalkBudget.Default;
        var beyond = (depth: budget.MaxDepth + 5, produced: budget.MaxProduced + 5, visited: budget.MaxVisited + 5,
            elapsedMs: budget.TimeBudget.TotalMilliseconds * 2);

        Assert.Equal(WalkStopReason.Depth, budget.Stop(beyond.depth, beyond.produced, beyond.visited, beyond.elapsedMs));
        Assert.All(
            new[] { WalkStopReason.Depth, WalkStopReason.Produced, WalkStopReason.Visited, WalkStopReason.Time },
            reason => Assert.NotEqual(WalkStopReason.None, reason));
    }

    [Fact]
    public void The_limits_are_configurable_so_a_test_can_exercise_a_small_tree()
    {
        var budget = WalkBudget.Default with { MaxVisited = 3, MaxProduced = 2, MaxDepth = 1, TimeBudget = TimeSpan.FromMilliseconds(10) };

        Assert.Equal(WalkStopReason.None, budget.Stop(depth: 1, produced: 1, visited: 2, elapsedMs: 9));
        Assert.Equal(WalkStopReason.Depth, budget.Stop(depth: 2, produced: 1, visited: 2, elapsedMs: 0));
        Assert.Equal(WalkStopReason.Produced, budget.Stop(depth: 1, produced: 2, visited: 2, elapsedMs: 0));
        Assert.Equal(WalkStopReason.Visited, budget.Stop(depth: 1, produced: 1, visited: 3, elapsedMs: 0));
        Assert.Equal(WalkStopReason.Time, budget.Stop(depth: 1, produced: 1, visited: 2, elapsedMs: 10));
    }
}

/// <summary>
/// Which controls the walk reads the text of.
/// <para>
/// The read is a cross-process RPC per control, and it is only worth paying for a control that holds text the
/// user typed. Static text is the most numerous element in a real interface, so reading all of it was the
/// largest avoidable cost in an observation.
/// </para>
/// </summary>
public sealed class UiElementValueReadTests
{
    [Theory]
    [InlineData("Edit")]
    [InlineData("Document")]
    [InlineData("ComboBox")]
    public void A_control_that_holds_text_is_read(string role) => Assert.True(UiaTree.ReadsValue(role));

    [Theory]
    [InlineData("Text")]
    [InlineData("Button")]
    [InlineData("ListItem")]
    [InlineData("CheckBox")]
    public void A_control_whose_text_is_never_acted_on_is_not_read(string role) =>
        Assert.False(UiaTree.ReadsValue(role));

    [Fact]
    public void The_document_text_surface_still_gets_its_value()
    {
        // This is the only channel by which an agent can verify that what it typed actually arrived: the
        // editor's content is not visible in a screenshot, and RichEdit exposes TextPattern rather than
        // ValuePattern. Dropping this role would silently remove that check.
        Assert.True(UiaTree.ReadsValue("Document"));
    }

    [Fact]
    public void The_value_is_read_for_exactly_the_roles_that_were_read_before()
    {
        // Anything else would be a behaviour change smuggled in with a performance fix.
        var readBefore = new[] { "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "ListItem", "MenuItem",
            "TabItem", "Hyperlink", "Slider", "Spinner", "TreeItem", "DataItem", "Text", "SplitButton", "Thumb",
            "Document" };

        Assert.Equal(
            readBefore.Where(role => role is "Edit" or "Document" or "ComboBox"),
            readBefore.Where(UiaTree.ReadsValue));
    }
}