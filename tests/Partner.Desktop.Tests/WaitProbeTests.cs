using System.Windows.Automation;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>Waiting for a control must not re-walk and re-lease the desktop several times a second.</summary>
public sealed class WaitProbeTests
{
    [Fact]
    public async Task Waiting_five_seconds_for_a_control_that_never_comes_walks_the_window_a_handful_of_times()
    {
        var walks = 0;
        using var automation = new DesktopAutomation(walk: (_, windowId) =>
        {
            Interlocked.Increment(ref walks);
            return (Array.Empty<UiElement>(), Array.Empty<AutomationElement>(), WalkStopReason.None);
        });

        var first = automation.Observe();
        if (first.Entities.Count == 0)
        {
            return;
        }

        var window = first.Entities[0].EntityId;
        var before = Volatile.Read(ref walks);

        var result = await new DesktopWaitTool(automation).InvokeAsync(
            $$"""{"kind":"element","entityId":"{{window}}","elementId":"{{window}}:99","timeoutMs":3000}""");

        Assert.False(result.Ok);

        // Polled every 250 ms with a full walk this was twelve; backing off it is about five, plus the one
        // observation made when the time ran out.
        Assert.InRange(Volatile.Read(ref walks) - before, 1, 7);
    }

    [Fact]
    public void Probing_reads_one_window_and_issues_nothing()
    {
        var walks = 0;
        using var automation = new DesktopAutomation(walk: (_, windowId) =>
        {
            Interlocked.Increment(ref walks);
            return (
                new[] { new UiElement($"{windowId}:0", windowId, "按钮", "Button", new PixelRect(0, 0, 10, 10), "d") },
                Array.Empty<AutomationElement>(),
                WalkStopReason.None);
        });

        var observed = automation.Observe();
        if (observed.Entities.Count == 0)
        {
            return;
        }

        var leasesBefore = automation.Leases.Count;
        var walksBefore = Volatile.Read(ref walks);
        var generationBefore = automation.LastObservation!.ObsId;

        var elements = automation.ProbeElements(observed.Entities[0].EntityId);

        Assert.Single(elements);
        Assert.Equal(1, Volatile.Read(ref walks) - walksBefore);
        Assert.Equal(leasesBefore, automation.Leases.Count);
        Assert.Equal(generationBefore, automation.LastObservation!.ObsId);
    }
}
