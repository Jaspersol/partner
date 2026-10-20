using System.Windows.Automation;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// What an observation pays for. The filter on <c>desktop_observe</c> decides which control trees are read at
/// all, not merely which are returned.
/// <para>
/// Counted through the walk seam against whatever windows the machine has: the assertions are about how many
/// walks a call made relative to a full one, never about a particular desktop.
/// </para>
/// </summary>
public sealed class ObserveWalkScopeTests
{
    private static UiElement Element(string windowId) => new(
        $"{windowId}:0",
        windowId,
        "control",
        "Button",
        new PixelRect(0, 0, 10, 10),
        "digest");

    private sealed class CountingDesktop : IDisposable
    {
        private int _walks;

        public CountingDesktop()
        {
            Automation = new DesktopAutomation(walk: (_, windowId) =>
            {
                Interlocked.Increment(ref _walks);
                return (new[] { Element(windowId) }, Array.Empty<AutomationElement>(), WalkStopReason.None);
            });
        }

        public DesktopAutomation Automation { get; }

        public int Walks => Volatile.Read(ref _walks);

        public void Dispose() => Automation.Dispose();
    }

    private static DesktopObserveTool Tool(DesktopAutomation automation) =>
        new(automation, new DesktopToolPolicy(Array.Empty<string>()));

    [Fact]
    public async Task A_window_named_by_entity_id_is_the_only_one_walked()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        if (full.Entities.Count < 2)
        {
            return; // nothing to narrow on a desktop with one window
        }

        var walkedForFull = desktop.Walks;
        var target = full.Entities[0].EntityId;

        var result = await Tool(desktop.Automation).InvokeAsync($$"""{"entityId":"{{target}}"}""");

        Assert.True(result.Ok, result.Content);
        Assert.Equal(1, desktop.Walks - walkedForFull);
    }

    [Fact]
    public async Task A_title_filter_walks_only_the_windows_it_matches()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        var titled = full.Entities.FirstOrDefault(entity => !string.IsNullOrWhiteSpace(entity.Label));
        if (titled is null || full.Entities.Count < 2)
        {
            return;
        }

        var walkedForFull = desktop.Walks;
        var needle = titled.Label!.Length > 4 ? titled.Label[..4] : titled.Label;
        var matching = full.Entities.Count(entity =>
            (entity.Label ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase));

        var result = await Tool(desktop.Automation).InvokeAsync($$"""{"titleContains":"{{needle}}"}""");

        Assert.True(result.Ok, result.Content);
        Assert.Equal(matching, desktop.Walks - walkedForFull);
    }

    [Fact]
    public async Task With_no_filter_only_the_front_and_the_last_acted_on_window_are_walked()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        if (full.Entities.Count < 3)
        {
            return;
        }

        var walkedForFull = desktop.Walks;

        var result = await Tool(desktop.Automation).InvokeAsync("{}");

        Assert.True(result.Ok, result.Content);

        // Nothing has been acted on yet, so the front window is the only one that qualifies.
        Assert.InRange(desktop.Walks - walkedForFull, 0, 1);
        Assert.Contains("elementsOmitted", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_true_still_reads_every_window()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        var walkedForFull = desktop.Walks;

        var result = await Tool(desktop.Automation).InvokeAsync("""{"all":true}""");

        Assert.True(result.Ok, result.Content);
        Assert.Equal(walkedForFull, desktop.Walks - walkedForFull);
        Assert.DoesNotContain("elementsOmitted", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_coordinate_action_reuses_the_controls_while_the_lease_is_live_and_the_window_has_not_moved()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        var target = full.Entities.FirstOrDefault(entity => !entity.IsMinimized);
        if (target is null)
        {
            return;
        }

        var walked = desktop.Walks;

        var fresh = desktop.Automation.ObserveForAct(target.EntityId, coordinateOnly: true, out var reused);

        Assert.True(reused);
        Assert.Equal(walked, desktop.Walks); // not one control tree was read
        Assert.NotNull(fresh.Find(target.EntityId));
        Assert.Equal(full.Elements.Count, fresh.Elements.Count);
    }

    [Fact]
    public void An_action_on_a_named_control_still_re_reads_that_window()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        var target = full.Entities.FirstOrDefault(entity => !entity.IsMinimized);
        if (target is null)
        {
            return;
        }

        var walked = desktop.Walks;

        desktop.Automation.ObserveForAct(target.EntityId, coordinateOnly: false, out var reused);

        Assert.False(reused);
        Assert.Equal(1, desktop.Walks - walked);
    }

    [Fact]
    public void A_window_that_was_not_walked_keeps_the_controls_it_was_last_shown()
    {
        using var desktop = new CountingDesktop();
        var full = desktop.Automation.Observe();
        if (full.Entities.Count < 2)
        {
            return;
        }

        var target = full.Entities[0].EntityId;
        var narrowed = desktop.Automation.Observe(walkWindow: window => window.Handle.ToString("x") == target);

        // Ids the model is holding must still resolve, and the observation says which windows were looked into.
        Assert.Equal(full.Elements.Count, narrowed.Elements.Count);
        Assert.Equal(new[] { target }, narrowed.WalkedWindows!.ToArray());
    }
}
