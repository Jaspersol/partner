namespace Partner.Desktop.Telemetry;

/// <summary>
/// One thing a desktop tool did. The fields mirror what the result text already carries — snapped, verify
/// decision, picture size — so aggregates can be computed without re-parsing result strings.
/// </summary>
/// <param name="Ts">When it happened (UTC).</param>
/// <param name="Tool">`desktop_act` / `desktop_observe` / `desktop_screenshot`.</param>
/// <param name="Kind">The act kind, when the tool is act.</param>
/// <param name="Target">Window title or element name the call resolved to; diagnostics only.</param>
/// <param name="Ok">Whether the tool reports success.</param>
/// <param name="Snapped">Whether a px/py call was upgraded to a control by the 8px snap.</param>
/// <param name="Verify">The post-action verification verdict ("AnyChange", "NoChange", "Indeterminate").</param>
/// <param name="MediaWidth">Width of the picture the answer carried, when one did.</param>
/// <param name="MediaHeight">Height of that picture.</param>
/// <param name="LatencyMs">How long the tool call took.</param>
/// <param name="StopReason">Why a window's UIA walk was cut off, when it was.</param>
public sealed record DesktopTelemetryEvent(
    DateTimeOffset Ts,
    string Tool,
    string? Kind,
    string? Target,
    bool Ok,
    bool? Snapped,
    string? Verify,
    int? MediaWidth,
    int? MediaHeight,
    double LatencyMs,
    string? StopReason);

/// <summary>
/// Where desktop telemetry goes. The tool loop is single-threaded per turn, so an in-memory list behind a
/// lock is enough for a sliding-window aggregate.
/// </summary>
public interface ITelemetrySink
{
    /// <summary>Records one event. Must never throw into the tool call: telemetry that can fail an action
    /// is worse than no telemetry.</summary>
    void Record(DesktopTelemetryEvent @event);
}

/// <summary>
/// The session-level sink: keeps recent events in memory, answers aggregates. Events older than the
/// window are dropped on write, so memory stays bounded by traffic, not by session length.
/// </summary>
public sealed class InMemoryTelemetrySink : ITelemetrySink
{
    private readonly object _gate = new();
    private readonly List<DesktopTelemetryEvent> _events = new();
    private readonly TimeProvider _clock;

    /// <summary>How long an event stays answerable. Retention is bounded so diagnostics cannot grow
    /// memory without limit.</summary>
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);

    private readonly TimeSpan _retention;

    /// <param name="clock">Time source; the retention cutoff is measured against it.</param>
    /// <param name="retention">
    /// How long an event stays answerable. The aggregate endpoint derives its window from this, so the two
    /// cannot disagree about the same events: a sink keeping an hour while the endpoint claims two would
    /// report a rate over events it never had, and the reverse would ignore half of what it holds.
    /// </param>
    public InMemoryTelemetrySink(TimeProvider? clock = null, TimeSpan? retention = null)
    {
        _clock = clock ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
    }

    /// <summary>How long an event stays answerable.</summary>
    public TimeSpan Retention => _retention;

    public void Record(DesktopTelemetryEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        try
        {
            lock (_gate)
            {
                _events.Add(@event);
                var cutoff = _clock.GetUtcNow() - _retention;
                _events.RemoveAll(e => e.Ts < cutoff);
            }
        }
        catch (Exception)
        {
            // Silent by contract: a failing sink must not fail the tool call that fed it.
        }
    }

    /// <summary>The events still inside the retention window, oldest first.</summary>
    public IReadOnlyList<DesktopTelemetryEvent> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToList();
        }
    }
}
