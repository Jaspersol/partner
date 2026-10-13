using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Telemetry;

namespace Partner.Desktop.Tests;

public class DesktopTelemetryTests
{
    private static DateTimeOffset At(int minute) =>
        new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    private static DesktopTelemetryEvent Act(
        string verify = "AnyChange",
        bool? snapped = null,
        int? width = null,
        double latencyMs = 10) => new(
        At(0), "desktop_act", "LeftClick", "按钮", true, snapped, verify, width, width, latencyMs, null);

    [Fact]
    public void Sink_keeps_recent_events_and_drops_expired_ones()
    {
        var clock = new FixedTimeClock(At(60));
        var sink = new InMemoryTelemetrySink(clock);

        sink.Record(Act());
        sink.Record(new DesktopTelemetryEvent(
            At(0) - InMemoryTelemetrySink.Retention - TimeSpan.FromMinutes(1),
            "desktop_observe", null, "9", true, null, null, null, null, 5, null));

        var snapshot = sink.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("desktop_act", snapshot[0].Tool);
    }

    [Fact]
    public void Sink_with_failing_clock_still_stores_the_event()
    {
        var clock = new ThrowingClock();
        var sink = new InMemoryTelemetrySink(clock);

        sink.Record(Act());

        // Retention cleanup cannot run without a clock, but the event itself must survive — a broken
        // clock degrades eviction, not storage.
        Assert.Single(sink.Snapshot());
    }

    [Fact]
    public void Observation_emits_one_event_with_walk_latency_and_truncation_reason()
    {
        var sink = new CaptureSink();
        using var automation = new DesktopAutomation(telemetry: sink);

        automation.Observe();

        var @event = Assert.Single(sink.Events);
        Assert.Equal("desktop_observe", @event.Tool);
        Assert.True(@event.Ok);
        Assert.True(@event.LatencyMs >= 0);
        Assert.Null(@event.StopReason);
        Assert.Null(@event.MediaWidth);
    }

    [Fact]
    public async Task Automation_act_without_observation_emits_no_event()
    {
        var sink = new CaptureSink();
        using var automation = new DesktopAutomation(telemetry: sink);

        var action = await automation.ActAsync(
            "ff",
            ActionKind.LeftClick,
            new ActionParams { X = 0, Y = 0 }).ConfigureAwait(true);

        // The automation emits only the observation side; the tool record — including refusals — is
        // DesktopActTool's, asserted through the API aggregation test.
        Assert.False(action.GuardPassed);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Picture_events_carry_their_dimensions()
    {
        var sink = new CaptureSink();
        using var automation = new DesktopAutomation(telemetry: sink);

        automation.RecordToolPicture("desktop_screenshot", "窗口", 1280, 800, zoom: false);
        automation.RecordToolPicture("desktop_screenshot", "窗口", 576, 576, zoom: true);

        Assert.All(sink.Events, e => Assert.Equal("desktop_screenshot", e.Tool));
        Assert.Equal(1280, sink.Events[0].MediaWidth);
        Assert.Equal(576, sink.Events[1].MediaWidth);
        Assert.Equal("zoom", sink.Events[1].Kind);
    }

    private sealed class CaptureSink : ITelemetrySink
    {
        public List<DesktopTelemetryEvent> Events { get; } = new();

        public void Record(DesktopTelemetryEvent @event) => Events.Add(@event);
    }

    private sealed class FixedTimeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException();
    }

    private sealed class ThrowingSink : ITelemetrySink
    {
        public void Record(DesktopTelemetryEvent @event) => throw new InvalidOperationException("sink broke");
    }

    [Fact]
    public void Broken_sink_does_not_fail_observation()
    {
        // The real call shape: the automation wraps Record in try/catch, so a sink that throws costs
        // the telemetry, not the observation.
        using var automation = new DesktopAutomation(telemetry: new ThrowingSink());

        var observation = automation.Observe();

        Assert.NotNull(observation);
    }

    [Fact]
    public void Broken_sink_does_not_fail_picture_recording()
    {
        using var automation = new DesktopAutomation(telemetry: new ThrowingSink());

        automation.RecordToolPicture("desktop_screenshot", "窗口", 1280, 800, zoom: false);
    }
}
