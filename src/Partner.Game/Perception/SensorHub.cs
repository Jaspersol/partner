using Partner.Game.Adapter;

namespace Partner.Game.Perception;

/// <summary>
/// Where a signal came from, which decides whether it may be debounced (design doc §2.6.1 ③, applied to the
/// game layer): sensor chatter is debounced, anything on the action or verification path never is.
/// </summary>
public enum SignalKind
{
    /// <summary>Environment/state observation: debounceable.</summary>
    Sensor,

    /// <summary>Guards a pending action: never debounced.</summary>
    ActionGuard,

    /// <summary>Verifies that an action landed: never debounced.</summary>
    PostCheck,

    /// <summary>Operator-injected signal: never debounced.</summary>
    Manual,
}

public sealed record RawSignal(string SourceId, SignalKind Kind, string Name, string Value, DateTimeOffset At);

public interface ISensorSource
{
    string SourceId { get; }

    /// <summary>Starts pushing signals. Implementations must not block the caller.</summary>
    void Attach(IGameAdapter adapter, Action<RawSignal> sink);

    void Detach();
}

/// <summary>
/// Fans several sensor sources into one stream, applying a trailing debounce per (source, name) — but only
/// to <see cref="SignalKind.Sensor"/>. Dropping an action or verification signal would silently delay or hide
/// an action, which is exactly the failure this split exists to prevent.
/// </summary>
public sealed class SensorHub
{
    private readonly TimeSpan _sensorDebounce;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastAccepted = new(StringComparer.Ordinal);
    private readonly List<ISensorSource> _sources = new();
    private readonly List<RawSignal> _accepted = new();
    private readonly List<RawSignal> _dropped = new();

    public SensorHub(TimeSpan? sensorDebounce = null, TimeProvider? clock = null)
    {
        _sensorDebounce = sensorDebounce ?? TimeSpan.FromMilliseconds(200);
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<RawSignal> Accepted => _accepted;

    public IReadOnlyList<RawSignal> Dropped => _dropped;

    public IReadOnlyList<ISensorSource> Sources => _sources;

    public void Attach(ISensorSource source, IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);

        _sources.Add(source);

        // Emit returns whether the signal survived the debounce; the source sink is a plain Action.
        source.Attach(adapter, signal => Emit(signal));
    }

    public void DetachAll()
    {
        foreach (var source in _sources)
        {
            source.Detach();
        }

        _sources.Clear();
    }

    /// <summary>Feeds one signal. Returns true when it was accepted, false when the debounce dropped it.</summary>
    public bool Emit(RawSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var at = signal.At == default ? _clock.GetUtcNow() : signal.At;

        if (signal.Kind == SignalKind.Sensor && _sensorDebounce > TimeSpan.Zero)
        {
            var key = $"{signal.SourceId}\u001f{signal.Name}";
            if (_lastAccepted.TryGetValue(key, out var last) && at - last < _sensorDebounce)
            {
                // Trailing debounce: the window restarts on every signal, so a burst collapses to its end.
                _lastAccepted[key] = at;
                _dropped.Add(signal with { At = at });
                return false;
            }

            _lastAccepted[key] = at;
        }

        _accepted.Add(signal with { At = at });
        return true;
    }
}

/// <summary>
/// The reflex → conscious gate (design doc §2.7): only signals the reflex layer explicitly escalates reach
/// the LLM. Everything else stays in the reaction path where it belongs.
/// </summary>
public sealed class ConsciousGate
{
    private readonly List<RawSignal> _escalated = new();

    public int EscalationCount => _escalated.Count;

    public void Escalate(RawSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _escalated.Add(signal);
    }

    /// <summary>True only for signals a reflex behavior decided to escalate.</summary>
    public bool ShouldForward(RawSignal signal) =>
        _escalated.Any(s => ReferenceEquals(s, signal)
            || (string.Equals(s.SourceId, signal.SourceId, StringComparison.Ordinal)
                && string.Equals(s.Name, signal.Name, StringComparison.Ordinal)
                && s.At == signal.At));

    public void Clear() => _escalated.Clear();
}
