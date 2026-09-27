using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop;

public sealed record DesktopAction
{
    public required string ActionId { get; init; }

    public required string ObsId { get; init; }

    public required string EntityId { get; init; }

    public required ActionKind Kind { get; init; }

    public required string ParamsJson { get; init; }

    public required DateTimeOffset LeaseExpiresAt { get; init; }

    public required bool GuardPassed { get; init; }

    public string? GuardReason { get; init; }

    public InjectionResult? Injection { get; init; }

    public VerifyResult? PostVerify { get; init; }
}

public sealed record DesktopLatency(double P50Ms, double P99Ms, double MaxMs);

/// <summary>
/// The L1 desktop facade (design doc §2.6): observe windows → issue a lease → inject input → verify.
/// <para>
/// Two rules are structural here, not advisory:
/// <list type="bullet">
///   <item><b>No action without a valid lease.</b> The lease is validated with the fixed four-step order
///   and a rejection never reaches <c>SendInput</c>.</item>
///   <item><b>No action is reported as successful without verification.</b> The result carries the
///   <see cref="VerifyResult"/>, including <c>Indeterminate</c> when it could not be measured.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DesktopAutomation : IDisposable
{
    private readonly IScreenCapture _capture;
    private readonly InputInjector _injector;
    private readonly LeaseStore _leases;
    private readonly LeaseTtlPolicy _ttlPolicy;
    private readonly PostActionVerifier _verifier;
    private readonly TimeProvider _clock;
    private readonly List<double> _actionLatenciesMs = new();
    private DesktopObservation? _lastObservation;
    private int _observationSequence;

    public DesktopAutomation(
        IScreenCapture? capture = null,
        LeaseTtlPolicy? ttlPolicy = null,
        TimeSpan? verifyBudget = null,
        TimeProvider? clock = null,
        InputInjector? injector = null)
    {
        _capture = capture ?? new GdiScreenCapture();
        _injector = injector ?? new InputInjector();
        _clock = clock ?? TimeProvider.System;
        _ttlPolicy = ttlPolicy ?? LeaseTtlPolicy.Default;
        _leases = new LeaseStore(_ttlPolicy);
        _verifier = new PostActionVerifier(_capture, verifyBudget);
    }

    public IScreenCapture Capture => _capture;

    /// <summary>
    /// The most recent observation, or null before the first one. Exposed so a caller can check *what* it is
    /// about to act on (a policy on window titles has to read the title) without re-observing — which would
    /// mint a fresh lease and make the lease mechanism vacuous.
    /// </summary>
    public DesktopObservation? LastObservation => _lastObservation;

    /// <summary>
    /// The window whose picture the model was last shown, if any. It is the referent for a click that names no
    /// window: the model worked its coordinates out from that picture, so when several windows overlap the
    /// point, that is the one it meant.
    /// </summary>
    public string? LastShownEntityId { get; private set; }

    /// <summary>Records which window was just shown to the model. Called by the screenshot tool.</summary>
    public void MarkShown(string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        LastShownEntityId = entityId;
    }

    public LeaseStore Leases => _leases;

    /// <summary>Observes the current windows and issues a fresh generation.</summary>
    public DesktopObservation Observe(ObserveScope scope = ObserveScope.Action, int payloadBytes = 0)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var windows = WindowEnumerator.List();
        watch.Stop();
        _leases.ObservedRoundTripMs = watch.Elapsed.TotalMilliseconds;

        var now = _clock.GetUtcNow();
        // The generation must be unique per observation: two observations that collide on the same
        // timestamp would make an old lease validate against a new observation.
        var sequence = Interlocked.Increment(ref _observationSequence);
        var obsId = $"obs-{sequence}-{now.UtcDateTime:O}";
        var generation = obsId;

        var entities = windows
            .Select(w => new UiEntity
            {
                EntityId = w.Handle.ToString("x"),
                Generation = generation,
                Role = "window",
                Label = w.Title,
                Value = w.ProcessName,
                IsForeground = w.IsForeground,
                IsMinimized = w.IsMinimized,
                Rect = new PixelRect(w.X, w.Y, w.Width, w.Height),
                Confidence = 1.0,
                EvidenceDigest = DesktopObservation.Digest(w.Title, w.ProcessName, new PixelRect(w.X, w.Y, w.Width, w.Height)),
                ObservedAtMs = now,
            })
            .ToList();


        // Elements are read per window and numbered inside this observation: an id is a position in this
        // answer, not a handle that survives a new one — which is exactly what makes a stale element detectable.
        var elements = new List<UiElement>();
        foreach (var entity in entities)
        {
            var handle = ParseHandle(entity.EntityId);
            var index = 0;

            foreach (var element in UiaTree.Read(handle, entity.EntityId))
            {
                elements.Add(element with { ElementId = $"{entity.EntityId}:{index++}" });
            }
        }

        // Issue one lease per entity so an action can be gated on it.
        var decision = _ttlPolicy.Compute(scope, entities.Count, payloadBytes, _leases.ObservedRoundTripMs);
        foreach (var entity in entities)
        {
            _leases.Issue(entity, obsId, now, decision.TtlMs);
        }

        _leases.EvictExpired(now);

        var observation = new DesktopObservation
        {
            ObsId = obsId,
            TargetKey = "desktop",
            Timestamp = now,
            Sources = new[] { "win32.enumwindows", "uia.controlview" },
            Entities = entities,
            Elements = elements,
        };

        _lastObservation = observation;
        return observation;
    }

    public Task<CaptureResult> CaptureAsync(string entityId, CancellationToken ct = default)
    {
        var observation = Observe();
        var entity = observation.Find(entityId)
            ?? throw new InvalidOperationException($"Unknown entity '{entityId}'.");

        return _capture.CaptureWindowAsync(ParseHandle(entity.EntityId), ct);
    }

    /// <summary>
    /// Puts the pointer on the point first, then wheels. A wheel goes to whatever window is under the cursor,
    /// so scrolling without moving is scrolling whatever the user's pointer happened to be over — the very
    /// thing the guard exists to prevent.
    /// </summary>
    private InjectionResult ScrollIn(ActionParams parameters)
    {
        var moved = _injector.MovePointer(parameters.X, parameters.Y);
        return moved.Ok ? _injector.Scroll(parameters.ScrollDelta) : moved;
    }

    /// <summary>
    /// Performs one action against <paramref name="entityId"/> using the <b>most recent observation</b>.
    /// <para>
    /// Deliberately does <b>not</b> re-observe: re-observing would mint a fresh lease and make the whole
    /// lease mechanism vacuous for this path. The caller observes, decides, then acts — and the TTL bounds
    /// how long that decision stays actionable. A later <see cref="Observe"/> bumps the generation and
    /// invalidates every lease issued against the previous one.
    /// </para>
    /// Refused actions come back with <see cref="DesktopAction.GuardPassed"/> = false and the rejection
    /// reason — nothing is injected.
    /// </summary>
    public async Task<DesktopAction> ActAsync(
        string entityId,
        ActionKind kind,
        ActionParams parameters,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(parameters);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var observation = _lastObservation;
        var actionId = $"act-{_clock.GetUtcNow().UtcDateTime:O}";

        if (observation is null)
        {
            return Refused(actionId, "(none)", entityId, kind, parameters, "no_observation");
        }

        var entity = observation.Find(entityId);
        if (entity is null)
        {
            return Refused(actionId, observation.ObsId, entityId, kind, parameters, "entity_not_found");
        }

        var lease = _leases.Peek(entityId);
        var validation = _leases.Validate(lease, observation.ObsId, observation.Entities, _clock.GetUtcNow());
        if (!validation.Ok)
        {
            return Refused(actionId, observation.ObsId, entityId, kind, parameters, validation.Reason.ToString());
        }

        var handle = ParseHandle(entityId);
        var before = await _capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false);

        var injection = kind switch
        {
            ActionKind.Focus => _injector.Focus(handle),
            ActionKind.MovePointer => _injector.MovePointer(parameters.X, parameters.Y),
            ActionKind.LeftClick => _injector.LeftClick(parameters.X, parameters.Y),
            ActionKind.RightClick => _injector.RightClick(parameters.X, parameters.Y),
            ActionKind.KeyPress => _injector.KeyPress(parameters.VirtualKey),
            ActionKind.TypeText => _injector.TypeText(parameters.Text ?? string.Empty),
            ActionKind.Scroll => ScrollIn(parameters),
            ActionKind.Drag => _injector.Drag(parameters.X, parameters.Y, parameters.ToX, parameters.ToY),
            _ => new InjectionResult(false, 0, $"unsupported action {kind}"),
        };

        var verify = await _verifier
            .VerifyAsync(handle, entity.Rect, before, ct)
            .ConfigureAwait(false);

        watch.Stop();
        _actionLatenciesMs.Add(watch.Elapsed.TotalMilliseconds);

        return new DesktopAction
        {
            ActionId = actionId,
            ObsId = observation.ObsId,
            EntityId = entityId,
            Kind = kind,
            ParamsJson = parameters.Describe(),
            LeaseExpiresAt = lease!.ExpiresAt,
            GuardPassed = true,
            Injection = injection,
            PostVerify = verify,
        };
    }

    /// <summary>Action latency statistics (design doc: latency must be measured, never assumed).</summary>
    public DesktopLatency Latency()
    {
        if (_actionLatenciesMs.Count == 0)
        {
            return new DesktopLatency(0, 0, 0);
        }

        var sorted = _actionLatenciesMs.OrderBy(v => v).ToList();
        return new DesktopLatency(
            sorted[sorted.Count / 2],
            sorted[(int)Math.Ceiling(sorted.Count * 0.99) - 1],
            sorted[^1]);
    }

    public void Dispose()
    {
        // Nothing unmanaged is held across calls: GDI objects are released inside each capture.
    }

    private DesktopAction Refused(
        string actionId,
        string obsId,
        string entityId,
        ActionKind kind,
        ActionParams parameters,
        string reason) => new()
        {
            ActionId = actionId,
            ObsId = obsId,
            EntityId = entityId,
            Kind = kind,
            ParamsJson = parameters.Describe(),
            LeaseExpiresAt = default,
            GuardPassed = false,
            GuardReason = reason,
        };

    private static nint ParseHandle(string entityId) =>
        nint.Parse(entityId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
}
