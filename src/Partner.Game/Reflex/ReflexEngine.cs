using Partner.Game.Adapter;
using Partner.Game.Perception;

namespace Partner.Game.Reflex;

/// <summary>
/// The reflex engine (design doc §2.7): pick at most one behavior per tick by score, hold the slot for the
/// whole async run, and never let the LLM into the reaction path.
/// <para>
/// Two invariants are structural:
/// <list type="bullet">
///   <item><b>One behavior at a time.</b> While an async behavior runs, the slot is locked and later ticks
///   do nothing — that is what keeps a 1.5 s survival action from being preempted mid-way.</item>
///   <item><b>The lock is bounded only by a deadlock breaker</b>, never by a short policy cap.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ReflexEngine
{
    private readonly IGameAdapter _adapter;
    private readonly ReflexOptions _options;
    private readonly TimeProvider _clock;
    private readonly List<IReflexBehavior> _behaviors = new();
    private readonly Dictionary<string, DateTimeOffset> _runHistory = new(StringComparer.Ordinal);
    private readonly List<ReflexDecision> _decisions = new();

    private DateTimeOffset? _activeBehaviorUntil;
    private string? _activeBehaviorId;

    public ReflexEngine(IGameAdapter adapter, ReflexOptions? options = null, TimeProvider? clock = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? new ReflexOptions();
        _clock = clock ?? TimeProvider.System;
        Context = new ReflexContext();
        Mode = _options.DefaultMode;
    }

    public ReflexContext Context { get; }

    public string Mode { get; private set; }

    public string? ActiveBehaviorId => _activeBehaviorId;

    /// <summary>True while an async behavior holds the slot.</summary>
    public bool SlotLocked => _activeBehaviorUntil is not null && _clock.GetUtcNow() < _activeBehaviorUntil.Value;

    /// <summary>Audit trail: which behavior ran, with what score, and whether it locked the slot.</summary>
    public IReadOnlyList<ReflexDecision> Decisions => _decisions;

    /// <summary>The async behavior currently driving the body, if any. Ticks do not await it by design.</summary>
    public Task? ActiveRun { get; private set; }

    /// <summary>
    /// Waits until no behavior is running. Needed for deterministic tests and for graceful shutdown — a tick
    /// deliberately returns while the behavior keeps working.
    /// </summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (ActiveRun is { } run && !run.IsCompleted)
        {
            await run.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Register(IReflexBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        if (_behaviors.Any(b => string.Equals(b.Id, behavior.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Behavior '{behavior.Id}' is already registered.");
        }

        _behaviors.Add(behavior);
    }

    public void TransitionMode(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        Mode = mode;
    }

    /// <summary>
    /// One tick. Returns the id of the behavior that ran, or null when the slot is locked, no behavior
    /// applies, or the chosen behavior finished synchronously.
    /// </summary>
    public async Task<string?> TickAsync(double? deltaMs = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        Context.UpdateNow(now);

        // The slot lock is the whole point: a running async behavior owns the body until it finishes.
        if (_activeBehaviorUntil is not null && now < _activeBehaviorUntil.Value)
        {
            return null;
        }

        _activeBehaviorId = null;
        _activeBehaviorUntil = null;

        if (_options.SelectMode is not null && Mode is not ("work" or "wander"))
        {
            Mode = _options.SelectMode(Context.GetSnapshot());
        }

        var snapshot = Context.GetSnapshot();
        var api = new ReflexApi(_adapter, Context, Gate);
        var delta = deltaMs ?? _options.DefaultDeltaMs;

        IReflexBehavior? best = null;
        var bestScore = 0d;

        foreach (var behavior in _behaviors)
        {
            if (behavior.Modes.Count > 0 && !behavior.Modes.Contains(Mode, StringComparer.Ordinal))
            {
                continue;
            }

            if (!behavior.When(snapshot, api))
            {
                continue;
            }

            var score = behavior.Score(snapshot, api);
            if (score <= 0)
            {
                continue;
            }

            if (behavior.CooldownMs > 0
                && _runHistory.TryGetValue(behavior.Id, out var lastRun)
                && (now - lastRun).TotalMilliseconds < behavior.CooldownMs)
            {
                continue;
            }

            if (best is null || score > bestScore)
            {
                best = behavior;
                bestScore = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        _activeBehaviorId = best.Id;
        _runHistory[best.Id] = now;
        Context.UpdateAutonomy(snapshot.Autonomy with { ReflexEngaged = true });

        try
        {
            var maybeTask = best.RunAsync(api, ct);

            if (maybeTask.IsCompleted)
            {
                // Completed synchronously: surface a failure now and free the slot immediately.
                await maybeTask.ConfigureAwait(false);
                _decisions.Add(new ReflexDecision(best.Id, bestScore, now, delta, LockedAsync: false));
                _activeBehaviorId = null;
                Context.UpdateAutonomy(Context.GetSnapshot().Autonomy with { ReflexEngaged = false });
                return best.Id;
            }

            // Still running: hold the slot and return now. Awaiting here would defeat the lock entirely —
            // the point is that the tick returns while the behavior keeps driving the body.
            ActiveRun = maybeTask;
            _activeBehaviorUntil = now.AddMilliseconds(Math.Max(delta, _options.SlotLockDeadlockMs));
            _decisions.Add(new ReflexDecision(best.Id, bestScore, now, delta, LockedAsync: true));

            _ = maybeTask.ContinueWith(
                completed =>
                {
                    _activeBehaviorUntil = null;
                    _activeBehaviorId = null;
                    ActiveRun = null;

                    var autonomy = Context.GetSnapshot().Autonomy;
                    Context.UpdateAutonomy(autonomy with
                    {
                        ReflexEngaged = false,
                        LastError = completed.IsFaulted
                            ? completed.Exception?.GetBaseException().Message ?? autonomy.LastError
                            : autonomy.LastError,
                    });
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return best.Id;
        }
        catch (Exception ex)
        {
            _activeBehaviorId = null;
            _activeBehaviorUntil = null;
            Context.UpdateAutonomy(Context.GetSnapshot().Autonomy with { LastError = ex.Message, ReflexEngaged = false });
            throw;
        }
    }

    /// <summary>Drives ticks on a fixed cadence. Real deployments are event-driven; this is the polling form.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await TickAsync(interval.TotalMilliseconds, ct).ConfigureAwait(false);
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    public ConsciousGate Gate { get; } = new();
}
