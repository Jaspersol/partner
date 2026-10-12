using Partner.Desktop.Capture;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// Waiting instead of observing on a loop. The sensor is the piece whose judgement could quietly be
/// wrong — a mid-repaint frame that looks still, a deadline that outlives the caller's own budget — so
/// the tests drive it with a scripted capture where "still" is a fact of the script, not of the machine.
/// </summary>
public sealed class DesktopWaitToolTests
{
    private static UiEntity Entity(string id = "1a2b", string label = "测试窗口") => new()
    {
        EntityId = id,
        Generation = "obs-1",
        Role = "window",
        Label = label,
        Value = "test",
        Rect = new PixelRect(100, 100, 400, 300),
        Confidence = 1.0,
        IdentityDigest = "digest-a",
        LayoutDigest = "layout-a",
        ProcessId = 4242,
        ObservedAtMs = DateTimeOffset.UnixEpoch,
    };

    private static DesktopObservation Observation() => new()
    {
        ObsId = "obs-1",
        TargetKey = "desktop",
        Timestamp = DateTimeOffset.UnixEpoch,
        Sources = new[] { "test" },
        Entities = new[] { Entity() },
    };

    [Fact]
    public void A_wait_is_a_budget_never_a_promise()
    {
        // The cap exists because the turn is single-reader: a wait with no ceiling parks the whole queue
        // behind it. Whatever the caller asked for, the sensor enforces the same wall.
        Assert.Equal(TimeSpan.FromSeconds(5), StabilitySensor.ClampTimeout(TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromSeconds(5), StabilitySensor.ClampTimeout(TimeSpan.FromSeconds(9)));

        // A zero ask is "the caller had no opinion": the budget still binds rather than waits forever.
        Assert.Equal(TimeSpan.FromSeconds(5), StabilitySensor.ClampTimeout(TimeSpan.Zero));
    }

    [Fact]
    public async Task Still_means_two_identical_frames_in_a_row_not_one()
    {
        // One identical hash is what a mid-repaint sample looks like; the measured reason two are required.
        // The scripted capture changes once and then holds: the first comparison must not be called stable,
        // the second — same hash as the third? no — must be.
        var schedule = new[]
        {
            MakeFrame(100, 100, 0xFF),  // round 1: content A
            MakeFrame(100, 100, 0xEE),  // round 2: content B (the page repainted)
            MakeFrame(100, 100, 0xEE),  // round 3: same as round 2 — stable
        };
        using var automation = new DesktopAutomation(capture: new ScriptedCapture(schedule));

        var sensor = new StabilitySensor(automation.Capture);
        var (stable, waited, _, _, rounds) = await sensor.WaitStillAsync(
            new nint(1), TimeSpan.FromSeconds(5));

        Assert.True(stable, $"stable={stable} rounds={rounds}");
        Assert.Equal(3, rounds);
        Assert.True(waited > 0);
    }

    [Fact]
    public async Task A_page_that_never_stops_moving_times_out_instead_of_hanging()
    {
        var flip = new FlipCapture();
        using var automation = new DesktopAutomation(capture: flip);

        var sensor = new StabilitySensor(automation.Capture);
        var (stable, _, _, _, _) = await sensor.WaitStillAsync(
            new nint(1), TimeSpan.FromMilliseconds(600));

        Assert.False(stable);
    }

    [Fact]
    public async Task An_element_that_is_already_there_comes_back_at_once()
    {
        // The common case after P0: the model already saw the button, acted, and the post picture showed
        // the result. A wait for the element should not spend a new walk on hesitation — one aimed walk,
        // answered with the fresh id.
        using var automation = new DesktopAutomation();
        var observation = automation.Observe();
        var target = observation.Entities.First(entity => !entity.IsMinimized);
        var element = observation.Elements.FirstOrDefault(e => e.WindowId == target.EntityId);
        if (element is null)
        {
            // A desktop session whose windows exposed no UIA elements at all (busy machine, elevated
            // apps): the run cannot manufacture one, and the smoke carries this check read-only.
            return;
        }

        var tool = new DesktopWaitTool(automation);
        var result = await tool.InvokeAsync($$"""
            {"kind":"element","entityId":"{{target.EntityId}}","elementId":"{{element.ElementId}}"}
            """);

        Assert.True(result.Ok, result.Content);
        Assert.Contains("控件已出现", result.Content, StringComparison.Ordinal);
        Assert.Contains("desktop_act 可直接用新 elementId", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_that_never_exists_times_out_with_the_candidates_named()
    {
        using var automation = new DesktopAutomation();
        var tool = new DesktopWaitTool(automation);

        var result = await tool.InvokeAsync(
            """{"kind":"window","titleContains":"д definitely-not-open-window-xyz","timeoutMs":200}""");

        Assert.False(result.Ok);
        Assert.Contains("definitely-not-open-window-xyz", result.Content, StringComparison.Ordinal);
        // The refusal has to say what *is* open, or the model only repeats.
        Assert.Contains("现在开着", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_wait_is_refused_not_guessed()
    {
        // A missing entityId is a malformed call, not a default: without it, "wait for the element" would
        // answer with whatever window the tool picked, and the model would act on that choice afterwards.
        using var automation = new DesktopAutomation();
        var tool = new DesktopWaitTool(automation);

        var missingId = await tool.InvokeAsync("""{"kind":"element"}""");
        Assert.False(missingId.Ok);
        Assert.Contains("entityId", missingId.Content, StringComparison.Ordinal);

        var missingElement = await tool.InvokeAsync(
            """{"kind":"element","entityId":"1a2b"}""");
        Assert.False(missingElement.Ok);
        Assert.Contains("elementId", missingElement.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wait_tools_are_read_only_and_registered_in_the_catalog()
    {
        Assert.True(DesktopToolCatalog.IsReadOnly("desktop_wait"));
        Assert.Contains("desktop_wait", DesktopToolCatalog.All);
    }

    [Fact]
    public async Task A_wait_on_an_unknown_window_or_kind_is_refused_with_what_would_work()
    {
        using var automation = new DesktopAutomation();
        var tool = new DesktopWaitTool(automation);

        var unknown = await tool.InvokeAsync("""{"kind":"element","entityId":"999","elementId":"9:0"}""");
        Assert.False(unknown.Ok);
        Assert.Contains("desktop_observe", unknown.Content, StringComparison.Ordinal);

        var badKind = await tool.InvokeAsync("""{"kind":"something"}""");
        Assert.False(badKind.Ok);
        Assert.Contains("element / window / stillFrame", badKind.Content, StringComparison.Ordinal);
    }

    /// <summary>The observation the first step of the wait needs, without a real desktop walk.</summary>
    private static CaptureResult MakeFrame(int width, int height, byte value)
    {
        var buffer = new byte[width * height * 4];
        Array.Fill(buffer, value);
        return new CaptureResult
        {
            Ok = true,
            Width = width,
            Height = height,
            Bgra = buffer,
            Method = "test.wait",
        };
    }

    /// <summary>Answers frames from a fixed schedule, so "still" is a fact of the script.</summary>
    private sealed class ScriptedCapture(CaptureResult[] frames) : IScreenCapture
    {
        private int _next;

        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) =>
            Task.FromResult(_next < frames.Length ? frames[_next++] : frames[^1]);

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }

    /// <summary>Every capture has new content: whatever happens, "still" never arrives.</summary>
    private sealed class FlipCapture : IScreenCapture
    {
        private long _n;

        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default)
        {
            var value = (byte)(Interlocked.Increment(ref _n) % 251);
            return Task.FromResult(MakeFrame(64, 64, value));
        }

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }
}
