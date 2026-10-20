using Partner.Game.Adapter;
using Partner.Game.Perception;

namespace Partner.Game.Reflex;

/// <summary>What a behavior can do: read state through the adapter, and escalate to the conscious layer.</summary>
public sealed record ReflexApi(IGameAdapter Adapter, ReflexContext Context, ConsciousGate Gate)
{
    /// <summary>Asks the conscious layer to look at this signal. Only escalated signals ever reach it.</summary>
    public void Escalate(RawSignal signal) => Gate.Escalate(signal);
}

/// <summary>
/// A reflex rule: applicable modes, a cheap applicability test, a score, and the action.
/// <para>
/// Scoring rather than ordering is what makes rules composable: a new behavior does not need to know about
/// the existing ones, it just has to score itself honestly.
/// </para>
/// </summary>
public interface IReflexBehavior
{
    string Id { get; }

    /// <summary>Modes this behavior may run in. Empty means "any mode".</summary>
    IReadOnlyList<string> Modes { get; }

    double CooldownMs { get; }

    bool When(ReflexContextState context, ReflexApi api);

    /// <summary>Higher wins. Zero or less means "not applicable".</summary>
    double Score(ReflexContextState context, ReflexApi api);

    Task RunAsync(ReflexApi api, CancellationToken ct);
}

public sealed record ReflexDecision(
    string BehaviorId,
    double Score,
    DateTimeOffset At,
    double DeltaMs,
    bool LockedAsync);

public sealed record ReflexOptions
{
    /// <summary>Delta assumed when the caller does not supply one.</summary>
    public double DefaultDeltaMs { get; init; } = 50;

    /// <summary>
    /// Upper bound on how long one async behavior may hold the slot. This is a <b>deadlock breaker</b>, not a
    /// policy: a short cap would release the slot mid-run and let another reflex preempt a long survival action.
    /// </summary>
    public double SlotLockDeadlockMs { get; init; } = 10_000;

    public string DefaultMode { get; init; } = "idle";

    /// <summary>Computes the mode from the context. When null the mode only changes via <c>TransitionMode</c>.</summary>
    public Func<ReflexContextState, string>? SelectMode { get; init; }
}
