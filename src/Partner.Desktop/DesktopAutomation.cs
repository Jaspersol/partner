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

    /// <summary>
    /// Whether the target had to be brought to the front before the input was injected, because something was
    /// covering the point. Reported because it changes what the user sees (their foreground window moves).
    /// </summary>
    public bool Raised { get; init; }

    public InjectionResult? Injection { get; init; }

    public VerifyResult? PostVerify { get; init; }
}

public sealed record DesktopLatency(double P50Ms, double P99Ms, double MaxMs);

/// <summary>
/// The L1 desktop facade (design doc §2.6): observe windows → issue a lease → inject input → verify.
/// <para>
/// Three rules are structural here, not advisory:
/// <list type="bullet">
///   <item><b>No action without a valid lease.</b> The lease is validated with the fixed four-step order
///   and a rejection never reaches <c>SendInput</c>.</item>
///   <item><b>Injected input goes only where the named window can receive it.</b> Pointer input is global,
///   so a point inside the target's rectangle can still be covered by another window; the point is
///   hit-tested first and a cover is refused by name rather than clicked through (see
///   <see cref="Occlusion"/>). A UI Automation pattern is exempt: it reaches the control itself, which is
///   the one way to press a control that is covered.</item>
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
    private readonly Func<int, int, PointTarget> _hitTest;
    private readonly List<double> _actionLatenciesMs = new();
    private DesktopObservation? _lastObservation;
    private int _observationSequence;

    public DesktopAutomation(
        IScreenCapture? capture = null,
        LeaseTtlPolicy? ttlPolicy = null,
        TimeSpan? verifyBudget = null,
        TimeProvider? clock = null,
        InputInjector? injector = null,
        Func<int, int, PointTarget>? hitTest = null)
    {
        _capture = capture ?? new GdiScreenCapture();
        _injector = injector ?? new InputInjector();
        _clock = clock ?? TimeProvider.System;
        _ttlPolicy = ttlPolicy ?? LeaseTtlPolicy.Default;
        _leases = new LeaseStore(_ttlPolicy);
        _verifier = new PostActionVerifier(_capture, verifyBudget);
        _hitTest = hitTest ?? HitTest.At;
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
    /// <param name="scope">What kind of view asked, which changes the lease TTL.</param>
    /// <param name="payloadBytes">How much was sent with it, which also changes the TTL.</param>
    /// <param name="elementsFor">
    /// When set, only this window's UI Automation tree is read; the other windows keep the elements the model
    /// was already shown. Reading every window's tree costs seconds (10.2 s for nine windows, measured), and
    /// an action re-checks one window — so the walk is aimed at that one. Ids are per-window indices, so the
    /// carried elements stay valid as references; the target's own are re-read, which is what makes a stale
    /// element detectable.
    /// </param>
    public DesktopObservation Observe(
        ObserveScope scope = ObserveScope.Action,
        int payloadBytes = 0,
        string? elementsFor = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var windows = WindowEnumerator.List();

        // Elements are read per window and numbered inside this observation: an id is a position in this
        // answer, not a handle that survives a new one — which is exactly what makes a stale element detectable.
        var elements = new List<UiElement>();
        foreach (var window in windows)
        {
            var windowId = window.Handle.ToString("x");

            if (elementsFor is not null && !string.Equals(windowId, elementsFor, StringComparison.Ordinal))
            {
                continue;
            }

            // Numbered by the same helper the picture's marks go through, so an id on a mark and the id in
            // this observation are the same control — not two independent walks that happen to agree today.
            elements.AddRange(UiaTree.ReadNumbered(window.Handle, windowId));
        }

        if (elementsFor is not null && _lastObservation is not null)
        {
            // The windows that were not re-walked keep what the model was shown, because those ids are what it
            // is holding — and a screenshot of one of them must still find its marks.
            elements.AddRange(_lastObservation.Elements.Where(element =>
                !string.Equals(element.WindowId, elementsFor, StringComparison.Ordinal)));
        }

        watch.Stop();
        _leases.ObservedRoundTripMs = watch.Elapsed.TotalMilliseconds;

        // Stamped *after* the walk, not before it. Reading the UIA trees of nine windows takes seconds —
        // Chromium needs a moment to publish its tree, one window hits the 40-element cap, and each window has
        // its own timeout — while the window enumeration is a few milliseconds. A lease stamped before the walk
        // begins its life already spent: TTL minus the observation, which for the 15 s base and a measured
        // 10.2 s observation left about five seconds, and any slower round expired the lease before the model
        // could act on it. That is the "Expired" refusal arriving on a lease the caller had just been issued.
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
                IdentityDigest = DesktopObservation.Identity(
                    w.Handle.ToString("x"), w.ProcessId, w.ProcessStartTicks, w.ProcessName),
                LayoutDigest = DesktopObservation.Layout(new PixelRect(w.X, w.Y, w.Width, w.Height)),
                ProcessId = w.ProcessId,
                ProcessStartTicks = w.ProcessStartTicks,
                ObservedAtMs = now,
            })
            .ToList();

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

        // A control the model named is acted on through UI Automation when the control supports a pattern for
        // it: that reaches the control itself, needs no foreground window, and does not depend on its screen
        // position being unchanged since the observation. Everything else — and every control with no
        // suitable pattern — goes through injected input, which is what a person at the keyboard would do.
        var injection = ElementPattern(handle, entityId, kind, parameters);
        var raised = false;

        if (injection is null)
        {
            // Before injecting: the point has to be somewhere the target can receive it. Injected input is
            // delivered by the OS to whatever window is under the point, and a point inside the target's
            // rectangle can still be covered by another application's window — in which case the click lands
            // there and the target's frame simply does not change, which reads as "the button did nothing".
            if (Occlusion(kind, parameters, handle, _hitTest) is { } covered)
            {
                // What the refusal used to tell the model to do, done here: the action was decided on this
                // window, and raising it is a means to that end rather than a different action — one approval
                // and one round trip instead of three. The result says so, because the user's foreground
                // window changes.
                if (TryRaise(kind, parameters, handle, covered) is { } failed)
                {
                    return Refused(actionId, observation.ObsId, entityId, kind, parameters, failed);
                }

                raised = true;
            }

            injection = Inject(kind, handle, parameters);
        }

        var verify = await _verifier
            // The target is the frame's own rectangle: the verifier compares frames, whose origin is the
            // window's top-left, and a screen rectangle there would intersect a different coordinate space.
            .VerifyAsync(handle, new PixelRect(0, 0, before.Width, before.Height), before, ct)
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
            Raised = raised,
            Injection = injection,
            PostVerify = verify,
        };
    }

    /// <summary>
    /// Acts on a named control through its UI Automation pattern, or null when that does not apply — the
    /// action names no control, or the control has no pattern that means what was asked. A pattern that was
    /// tried and failed is reported as a failure rather than retried as a blind coordinate click: if the
    /// control refused to be invoked, clicking its centre is not going to work either.
    /// </summary>
    private static InjectionResult? ElementPattern(nint handle, string entityId, ActionKind kind, ActionParams parameters)
    {
        if (parameters.ElementId is not { Length: > 0 } elementId)
        {
            return null;
        }

        var invocation = UiaTree.TryAct(handle, entityId, elementId, kind);

        return invocation is null
            ? null
            : new InjectionResult(invocation.Ok, 0, $"{invocation.Method}：{invocation.Detail}");
    }

    /// <summary>
    /// Whether a positional action would land somewhere other than the window it names.
    /// <para>
    /// Pointer input is global: the OS delivers it to whatever top-level window is under the point, and
    /// nothing in the call says which window was meant. So a point can be inside the target's rectangle and
    /// still be covered by another application's window — the agent's own UI is the usual cover, because
    /// that is what the user is looking at — in which case the click is delivered there, the target never
    /// sees it, and the injection still reports success. Refusing, and naming what is in the way, is what
    /// turns that silence into something the model can act on: focus the target first, which raises it over
    /// the cover.
    /// </para>
    /// A window the target owns is not a cover: menus, popups and dialogs of the application answer their
    /// owner, and a click on one is what a person's click would do. The point of the check is to stop a click
    /// from silently landing on a window the model did not name — including another window of the same
    /// program, which is a different window however it was launched.
    /// </summary>
    /// <param name="at">The top-level window at a point — injected so the rule can be exercised without a
    /// desktop.</param>
    public static string? Occlusion(
        ActionKind kind,
        ActionParams parameters,
        nint handle,
        Func<int, int, PointTarget> at)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(at);

        foreach (var (x, y) in DeliveryPoints(kind, parameters))
        {
            var landed = at(x, y);

            if (landed.Handle == handle || landed.RootOwner == handle)
            {
                continue;
            }

            var who = landed.Handle == 0
                ? "这个点上没有窗口"
                : $"这个点上是 '{landed.Title}'（进程 {landed.ProcessId}）";

            return $"({x},{y}) {who}，不是目标窗口：点会落在它上面，目标收不到。"
                + "先 desktop_act focus 目标窗口把它带到前台，再点；或把挡住的窗口挪开。";
        }

        return null;
    }

    /// <summary>
    /// Whether every point a positional action delivers to is inside the rectangle. Re-checked after a window
    /// is raised, because focusing can restore or move it — and a coordinate that no longer lands on the
    /// target is worse than a refusal.
    /// </summary>
    public static bool PointsInside(ActionKind kind, ActionParams parameters, PixelRect rect)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return DeliveryPoints(kind, parameters).All(point => rect.Contains(point.X, point.Y));
    }

    /// <summary>
    /// The points an action delivers to. Only the kinds that deliver to a coordinate are listed:
    /// <c>MovePointer</c>'s whole contract is "put the pointer there", which no window can swallow, and
    /// <c>Focus</c> carries no point at all.
    /// </summary>
    private static IReadOnlyList<(int X, int Y)> DeliveryPoints(ActionKind kind, ActionParams parameters) => kind switch
    {
        ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll =>
            new[] { (parameters.X, parameters.Y) },
        ActionKind.Drag =>
            new[] { (parameters.X, parameters.Y), (parameters.ToX, parameters.ToY) },
        _ => Array.Empty<(int X, int Y)>(),
    };

    /// <summary>
    /// Brings the target over whatever covers the point, or explains why that did not work.
    /// <para>
    /// Focus is not only about the keyboard: a window can be the active one and still be covered, and then the
    /// click lands on the cover. Raising it is the move the refusal used to ask the model to make, done in the
    /// same action because the action was already decided on this window. It is not silent — the result says
    /// the window was raised — and it is bounded: a window that moved while being raised (a restore) makes
    /// the coordinates stale, and then the action is refused rather than aimed at a rectangle that no longer
    /// holds the point.
    /// </para>
    /// </summary>
    /// <returns>Null when the point is now reachable, or the reason it is not.</returns>
    private string? TryRaise(ActionKind kind, ActionParams parameters, nint handle, string covered)
    {
        var focused = _injector.Focus(handle);
        if (!focused.Ok)
        {
            return $"{covered}（尝试把它带到前台失败：{focused.Detail}）";
        }

        var bounds = WindowEnumerator.GetBounds(handle);
        if (!PointsInside(kind, parameters, new PixelRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height)))
        {
            return "窗口在带到前台时移动或还原了，之前的坐标可能已经过期：重新 desktop_observe 再决定";
        }

        return Occlusion(kind, parameters, handle, _hitTest) is { } remaining
            ? $"{remaining}（已把它带到前台，仍然被挡住：把挡住的窗口挪开）"
            : null;
    }

    /// <summary>Injected input — the path used for everything a pattern cannot express.</summary>
    private InjectionResult Inject(ActionKind kind, nint handle, ActionParams parameters) => kind switch
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
