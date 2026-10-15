using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Telemetry;
using Partner.Desktop.Tools;
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
/// What one screenshot offered the model, kept so the server — not the model — owns the conversion from the
/// picture the model is looking at to the screen the pointer moves on.
/// <para>
/// The model reports a position as pixels <b>on the picture it holds</b> (px/py + the picture's mediaId);
/// the server maps it through the rectangle the picture was taken at. Reading positions off a picture is
/// what a vision model is good at; multiplying by window offsets is not, and a measured failure had a
/// correct verbal description of a play button land 334 px away from it in empty background. The scale for
/// the two axes is carried separately, because a capture cropped to the visible frame and a rescale to the
/// token budget do not have to keep the aspect they started with.
/// </para>
/// </summary>
/// <param name="EntityId">The window the picture is a view of.</param>
/// <param name="ScreenRect">Where the picture's top-left pixel sat on the screen when it was taken.</param>
/// <param name="PictureWidth">The encoded picture's width, in picture pixels.</param>
/// <param name="PictureHeight">The encoded picture's height, in picture pixels.</param>
/// <param name="ObsId">The observation the picture was taken against — the audit trail.</param>
public sealed record ShownPicture(
    string EntityId,
    PixelRect ScreenRect,
    int PictureWidth,
    int PictureHeight,
    string ObsId)
{
    /// <summary>Screen pixels per picture pixel, per axis.</summary>
    public (double X, double Y) Scale => (
        PictureWidth > 0 ? ScreenRect.Width / (double)PictureWidth : 1,
        PictureHeight > 0 ? ScreenRect.Height / (double)PictureHeight : 1);
}

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

    /// <summary>Overridable so the cheap capture path is exercised without a real foreground window.</summary>
    private readonly Func<nint, bool> _isForeground;

    /// <summary>Overridable alongside <see cref="_isForeground"/>, for the same reason.</summary>
    private readonly Func<VirtualDesktop> _virtualDesktop;

    /// <summary>
    /// One window's UI Automation walk. Overridable so an observation can be driven — and its truncation reason
    /// chosen — without a real desktop, which is the only way to exercise the concurrent case.
    /// </summary>
    private readonly Func<nint, string, (IReadOnlyList<UiElement> Elements, IReadOnlyList<System.Windows.Automation.AutomationElement> Nodes, WalkStopReason Stopped)> _walk;

    /// <summary>
    /// The DOM side of a Chromium window, and the ports of the ones this agent launched. Null-safe everywhere:
    /// a window without a debug port simply has no DOM elements, which is the coordinate path's window.
    /// </summary>
    private readonly CdpDomSource? _cdp;

    private readonly CdpRegistry? _cdpPorts;

    /// <summary>
    /// The launch tool, when it shares the registry above. Null means this automation did not open the ports,
    /// which is a configuration, not a failure.
    /// </summary>
    private readonly DesktopLaunchTool? _launch;

    /// <summary>Overridable so the DOM path is exercised without a browser.</summary>
    private readonly Func<nint, (int X, int Y)> _clientOrigin;

    /// <summary>
    /// The UIPI gate, called before any <c>SendInput</c>-carried action: null uses the real one, and an
    /// override lets tests answer for it without a real token.
    /// </summary>
    private readonly Func<nint, string?> _integrity;

    /// <summary>
    /// Where tool measurements land, when there is one. The automation emits only the walk side (latency,
    /// truncation) — tool results belong to the tools — and a null sink means "no telemetry", which every
    /// caller is expected to treat as the ordinary case.
    /// </summary>
    private readonly ITelemetrySink? _telemetry;

    /// <param name="cdp">The DOM source; omit it to leave Chromium windows on the coordinate path.</param>
    /// <param name="cdpPorts">Which processes opened a debug port, recorded at launch.</param>
    public CdpDomSource? Cdp => _cdp;

    public CdpRegistry? CdpPorts => _cdpPorts;

    /// <summary>
    /// A launch tool wired to the same port registry, or null when this automation did not open the ports.
    /// <para>
    /// The tool takes the registry at construction rather than being handed one afterwards: a port is recorded
    /// by the launch that opened it, and a launch tool that could not write to the registry would produce a
    /// window whose DOM stays invisible for the rest of the process's life — a failure that looks exactly like
    /// "this application has no controls".
    /// </para>
    /// </summary>
    public DesktopLaunchTool? LaunchTool => _launch
        ?? (_cdpPorts is null ? null : new DesktopLaunchTool(startMenuRoots: null, ports: _cdpPorts));
    private readonly List<double> _actionLatenciesMs = new();

    /// <summary>
    /// The last walk of each window: its numbered elements and the live controls behind them.
    /// <para>
    /// Kept so pressing a control by id does not walk the same UI Automation tree twice. The observation the
    /// action is checked against already re-walks the target window, and that walk is the fresher of the two —
    /// re-walking inside the act only repeated work that had just been done, at a cross-process RPC per node.
    /// </para>
    /// <para>
    /// Published as a unit together with the truncation reasons (<see cref="WalkSnapshot"/>) rather than written
    /// in place: the step budget allows parallel tool calls, so two observations can be in flight at once, and a
    /// dictionary that both of them mutate mid-walk hands one call's truncation reasons to the other's entities.
    /// </para>
    /// </summary>
    private WalkSnapshot _walkState = WalkSnapshot.Empty;

    /// <summary>
    /// One observation's walks and truncation reasons, immutable once published.
    /// <para>
    /// The two belong together: they are replaced at exactly the same moments — a re-observation refreshes both —
    /// and reading one from a different observation than the other is what produced a truncation count belonging
    /// to somebody else's walk.
    /// </para>
    /// </summary>
    private sealed record WalkSnapshot(
        IReadOnlyDictionary<string, (IReadOnlyList<UiElement> Elements, IReadOnlyList<System.Windows.Automation.AutomationElement> Nodes)> Walks,
        IReadOnlyDictionary<string, WalkStopReason> Truncated)
    {
        public static WalkSnapshot Empty { get; } = new(
            new Dictionary<string, (IReadOnlyList<UiElement>, IReadOnlyList<System.Windows.Automation.AutomationElement>)>(StringComparer.Ordinal),
            new Dictionary<string, WalkStopReason>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether a stop reason means "there may be more controls in this window".
    /// <para>
    /// Depth and the per-window control cap are ordinary — every deep tree stops at the depth limit, and a
    /// control-rich window stops at the cap by design — and reporting those would put a warning on almost every
    /// window. Being cut off on nodes visited or on time is different: that is the walk running out of budget
    /// with the tree unfinished, which is exactly where controls disappear.
    /// </para>
    /// </summary>
    internal static WalkStopReason? CutOff(WalkStopReason stopped) => stopped switch
    {
        WalkStopReason.Visited or WalkStopReason.Time => stopped,
        _ => null,
    };

    private volatile DesktopObservation? _lastObservation;
    private int _observationSequence;

    public DesktopAutomation(
        IScreenCapture? capture = null,
        LeaseTtlPolicy? ttlPolicy = null,
        TimeSpan? verifyBudget = null,
        TimeProvider? clock = null,
        InputInjector? injector = null,
        Func<int, int, PointTarget>? hitTest = null,
        Func<nint, bool>? isForeground = null,
        Func<VirtualDesktop>? virtualDesktop = null,
        CdpDomSource? cdp = null,
        CdpRegistry? cdpPorts = null,
        Func<nint, (int X, int Y)>? clientOrigin = null,
        DesktopLaunchTool? desktopLaunch = null,
        Func<nint, string?>? integrityRefusal = null,
        ITelemetrySink? telemetry = null,
        Func<nint, string, (IReadOnlyList<UiElement> Elements, IReadOnlyList<System.Windows.Automation.AutomationElement> Nodes, WalkStopReason Stopped)>? walk = null)
    {
        // Both seams exist so the observation's shared state can be exercised without a real desktop: a walk
        // that returns a chosen truncation reason is the only way to drive the concurrent case, and window
        // enumeration is a real API that would otherwise decide which windows the test is about.
        _walk = walk ?? UiaTree.ReadNumberedWithNodes;
        _capture = capture ?? new GdiScreenCapture();
        _injector = injector ?? new InputInjector();
        _clock = clock ?? TimeProvider.System;
        _ttlPolicy = ttlPolicy ?? LeaseTtlPolicy.Default;
        _leases = new LeaseStore(_ttlPolicy);
        _verifier = new PostActionVerifier(_capture, verifyBudget);
        _hitTest = hitTest ?? HitTest.At;

        // Spelled out rather than inlined: the null-coalescing operator cannot mix a delegate and a lambda
        // here without the target type being inferred from the wrong side.
        var foreground = isForeground ?? new Func<nint, bool>(handle => NativeMethods.GetForegroundWindow() == handle);
        _isForeground = foreground;

        _virtualDesktop = virtualDesktop ?? VirtualDesktop.Current;

        _cdp = cdp;
        _cdpPorts = cdpPorts;
        _launch = desktopLaunch;

        // The page reports viewport coordinates; the client area's screen origin is the only thing that turns
        // them into something a person could have clicked. A negative one means the window could not say where
        // its content starts, which is a reason not to guess rather than a read failure.
        _clientOrigin = clientOrigin ?? (handle =>
        {
            var origin = new NativeMethods.Point { X = 0, Y = 0 };
            return NativeMethods.ClientToScreen(handle, ref origin) ? (origin.X, origin.Y) : (X: -1, Y: -1);
        });

        _integrity = integrityRefusal ?? new IntegrityGate().Refusal;
        _telemetry = telemetry;
        _clockLocal = clock ?? TimeProvider.System;
    }

    private readonly TimeProvider _clockLocal;

    public IScreenCapture Capture => _capture;

    /// <summary>The telemetry sink, when one was wired; the tools record their own halves through it.</summary>
    public ITelemetrySink? Telemetry => _telemetry;

    /// <summary>
    /// The whole virtual desktop in one frame, wherever it starts and however it spans monitors.
    /// <para>
    /// This is the observation level under "window": the taskbar, the tray, notification banners, desktop
    /// icons — all the things that belong to no top-level window and therefore never appeared in an
    /// observation. The bounds are read now (they can change with the monitor layout) and travel with the
    /// frame as its <c>ScreenRect</c>, which is the same contract every window capture keeps
    /// </para>
    /// </summary>
    public async Task<CaptureResult> CaptureDesktopAsync(CancellationToken ct = default)
    {
        var desktop = _virtualDesktop();
        var frame = await _capture.CaptureScreenRegionAsync(
            desktop.X, desktop.Y, desktop.Width, desktop.Height, ct).ConfigureAwait(false);

        return frame.Ok
            ? frame with { ScreenRect = new PixelRect(desktop.X, desktop.Y, desktop.Width, desktop.Height) }
            : frame;
    }

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

    /// <summary>How many pictures a medium id stays addressable by. The model needs the ones it is reasoning
    /// about now; older screenshots age out rather than having the map grow with the session.</summary>
    private const int PictureMemory = 8;

    private readonly object _picturesGate = new();

    /// <summary>mediaId → the picture the server created it from, oldest evicted.</summary>
    private readonly Dictionary<string, ShownPicture> _pictures = new(StringComparer.Ordinal);

    private readonly List<string> _pictureOrder = [];

    /// <summary>Records one picture the model was just shown, addressable by its media id, so the server owns
    /// the picture→screen conversion for it (see <see cref="ShownPicture"/>).</summary>
    public void RecordPicture(string mediaId, ShownPicture picture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);
        ArgumentNullException.ThrowIfNull(picture);

        lock (_picturesGate)
        {
            if (_pictures.Remove(mediaId))
            {
                _pictureOrder.Remove(mediaId);
            }

            _pictureOrder.Add(mediaId);
            _pictures[mediaId] = picture;

            while (_pictureOrder.Count > PictureMemory)
            {
                var evicted = _pictureOrder[0];
                _pictureOrder.RemoveAt(0);
                _pictures.Remove(evicted);
            }
        }
    }

    /// <summary>The picture a media id was created from, or null when it has aged out (or was never one).
    /// A null here is reported to the model rather than resolved against some newer picture: reporting
    /// coordinates taken off photo A against photo B is exactly the misaligned click this mechanism exists
    /// to prevent.</summary>
    public ShownPicture? PictureOf(string mediaId)
    {
        if (string.IsNullOrEmpty(mediaId))
        {
            return null;
        }

        lock (_picturesGate)
        {
            return _pictures.TryGetValue(mediaId, out var picture) ? picture : null;
        }
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

        // This call's own walks and truncation reasons, seeded from the published state and published back at
        // the end. Two observations may be in flight (the step budget allows parallel tool calls), so neither
        // the dictionaries nor the entries inside them are written in place: a full walk replaces both wholesale,
        // a one-window walk carries the other windows' entries forward untouched — which is the same lockstep
        // pair the previous walk produced, and the invariant an element id's meaning rests on.
        var walks = new Dictionary<string, (IReadOnlyList<UiElement> Elements, IReadOnlyList<System.Windows.Automation.AutomationElement> Nodes)>(StringComparer.Ordinal);
        var truncated = new Dictionary<string, WalkStopReason>(StringComparer.Ordinal);
        if (elementsFor is not null)
        {
            foreach (var (id, walk) in _walkState.Walks)
            {
                walks[id] = walk;
            }

            foreach (var (id, reason) in _walkState.Truncated)
            {
                truncated[id] = reason;
            }
        }

        foreach (var window in windows)
        {
            var windowId = window.Handle.ToString("x");

            if (elementsFor is not null && !string.Equals(windowId, elementsFor, StringComparison.Ordinal))
            {
                continue;
            }

            // Numbered by the same helper the picture's marks go through, so an id on a mark and the id in
            // this observation are the same control — not two independent walks that happen to agree today.
            var (numbered, nodes, stopped) = _walk(window.Handle, windowId);
            elements.AddRange(numbered);

            walks[windowId] = (numbered, nodes);

            // A walk cut off on nodes or time has not seen the whole window, and the model has to be able to
            // tell that from a window that simply has few controls. Otherwise a control that exists goes
            // missing with nothing in the answer to say why.
            if (CutOff(stopped) is { } reason)
            {
                truncated[windowId] = reason;
            }
            else
            {
                truncated.Remove(windowId);
            }
        }

        // Published before the observation that reads them: the entities below and any act that follows see one
        // consistent pair. A full walk drops the previous snapshot entirely, which is what keeps a closed
        // window's live controls from being held for the life of the process.
        _walkState = new WalkSnapshot(walks, truncated);

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
        // its own node and time budget — while the window enumeration is a few milliseconds. A lease stamped before the walk
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
                ElementsTruncated = truncated.TryGetValue(w.Handle.ToString("x"), out var cutOff),
                ElementsTruncation = truncated.GetValueOrDefault(w.Handle.ToString("x")),
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
        RecordTelemetry(observation, watch.Elapsed.TotalMilliseconds);
        return observation;
    }

    /// <summary>
    /// One aggregate event per observation: latency, plus a truncation reason when <i>any</i> window was
    /// cut off on nodes or time. Per-window truncation details live on the entities themselves; the
    /// aggregate answers "how often is the walk budget losing controls", which is the tuning question.
    /// </summary>
    private void RecordTelemetry(DesktopObservation observation, double latencyMs)
    {
        if (_telemetry is null)
        {
            return;
        }

        var stopReason = observation.Entities
            .Select(entity => entity.ElementsTruncation)
            .Where(reason => reason is WalkStopReason.Visited or WalkStopReason.Time)
            .Select(reason => reason.ToString())
            .FirstOrDefault();

        try
        {
            _telemetry.Record(new DesktopTelemetryEvent(
                _clockLocal.GetUtcNow(),
                "desktop_observe",
                Kind: null,
                Target: observation.Entities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Ok: true,
                Snapped: null,
                Verify: null,
                MediaWidth: null,
                MediaHeight: null,
                latencyMs,
                stopReason));
        }
        catch (Exception)
        {
            // Telemetry must not fail the observation that fed it.
        }
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

        // Focus/aim kinds change where the next action goes, not what the window paints, so there is no
        // before-frame to keep and nothing to verify against. Capturing and diffing them anyway produced
        // "校验：画面无变化" on actions that never promised a visual effect — and a real session read that
        // sentence as its click having failed.
        var verifiesAfterwards = NeedsVerification(kind);
        var before = verifiesAfterwards
            ? await _capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false)
            : CaptureResult.Failed("skipped", "aim-only kind does not need a before-frame");

        // A control the model named is acted on through UI Automation when the control supports a pattern for
        // it: that reaches the control itself, needs no foreground window, and does not depend on its screen
        // position being unchanged since the observation. Everything else — and every control with no
        // suitable pattern — goes through injected input, which is what a person at the keyboard would do.
        //
        // A page's own control comes first, because a Chromium window's controls are not UI Automation
        // controls at all: there, "the control" is a DOM node and the way to press it is to press that node.
        var injection = parameters.ElementId is { Length: > 0 } domElementId
            ? await TryActDomAsync(handle, entityId, domElementId, kind, ct).ConfigureAwait(false) is { } dom
                ? new InjectionResult(dom.Ok, 0, $"{dom.Method}：{dom.Detail}")
                : ElementPattern(handle, entityId, kind, parameters, WalkOf(entityId))
            : ElementPattern(handle, entityId, kind, parameters, WalkOf(entityId));
        var raised = false;

        if (injection is null)
        {
            // UIPI first, before anything touches the user's window order: an elevated target swallows this
            // process's SendInput silently, so focusing and raising it would move the user's foreground for an
            // action that was never going to land. A UI Automation pattern — the path above — crosses integrity
            // levels fine and so is not gated; this is a gate on the one communication channel that would be
            // swallowed, not on reaching the window at all.
            if (DeliversInput(kind) && _integrity(handle) is { } uiPiDenied)
            {
                return Refused(actionId, observation.ObsId, entityId, kind, parameters, uiPiDenied);
            }

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

        var verify = verifiesAfterwards
            ? await VerifyAsync(handle, injection, parameters, before, observation, ct)
                .ConfigureAwait(false)
            : null;

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
    /// Judges the action against the part of the window it could plausibly have changed.
    /// <para>
    /// A control the model named is judged by <i>that control's rectangle</i>, not the window's. The decision
    /// is a fraction of the judged area, so a whole-window target makes a small control's change fall under
    /// the threshold: a button that repainted 60×30 in a 1200×800 window changed 1800 pixels against a
    /// 4800-pixel line, and the click that worked came back as "画面无变化（效果未被确认）".
    /// </para>
    /// <para>
    /// Two cases fall back to the whole frame, deliberately. A UI Automation pattern is not local — invoking a
    /// menu button opens a menu anywhere in the window, and judging only the button would report the menu as
    /// "no change". And a keystroke or a typed string has no rectangle of its own at all: what it changed is
    /// wherever the text landed, which is the window.
    /// </para>
    /// </summary>
    private Task<VerifyResult> VerifyAsync(
        nint handle,
        InjectionResult injection,
        ActionParams parameters,
        CaptureResult before,
        DesktopObservation observation,
        CancellationToken ct)
    {
        // The frame's own rectangle, in frame coordinates: the verifier compares frames, whose origin is the
        // window's top-left, and a screen rectangle there would intersect a different coordinate space.
        var whole = new PixelRect(0, 0, before.Width, before.Height);

        var viaPattern = injection.Detail.StartsWith("uia.", StringComparison.Ordinal);

        var region = viaPattern || parameters.ElementId is not { Length: > 0 } elementId
            ? null
            : observation.FindElement(elementId)?.Rect;

        return region is { } named && !named.IsEmpty
            ? _verifier.VerifyScreenRegionAsync(handle, named, before, ct, CanCaptureRegion(handle, named))
            : _verifier.VerifyAsync(handle, whole, before, ct);
    }

    /// <summary>
    /// Whether the region's pixels can be copied off the screen instead of re-rendering the whole window.
    /// <para>
    /// Only when the target is the foreground window. A screen BitBlt copies whatever is actually at those
    /// pixels, so anything drawn on top — another application, a tooltip, this agent's own window — would be
    /// compared against the target's own content and read as a change the target never made. The foreground
    /// window is the one thing that guarantees nothing is on top of it, and it is also the state the occlusion
    /// guard leaves a coordinate action in, so the cheap path is the common case rather than a special one.
    /// </para>
    /// </summary>
    private bool CanCaptureRegion(nint handle, PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return false;
        }

        // Off the virtual desktop cannot be copied, and a minimized window's rectangle is a -32000 stub.
        return !NativeMethods.IsIconic(handle)
            && _isForeground(handle)
            && _virtualDesktop().Contains(region.X, region.Y);
    }

    /// <summary>
    /// The last walk of one window, when this process has one — so a pattern call can use it instead of
    /// walking the same tree again. Null when the window was never walked here, and then the act walks it.
    /// </summary>
    private (IReadOnlyList<UiElement> Elements, IReadOnlyList<System.Windows.Automation.AutomationElement> Nodes)? WalkOf(
        string entityId) => _walkState.Walks.TryGetValue(entityId, out var walk) ? walk : null;

    /// <summary>
    /// Adds the page's own controls to a window that UI Automation cannot see into.
    /// <para>
    /// Separate from <see cref="Observe"/> because reading a page is a network round trip and the observation
    /// is a synchronous walk over local windows. The two are also different sources: a Chromium window has one
    /// control as far as UI Automation is concerned — the webview's document surface — and dozens as far as
    /// its DOM is concerned, and both are wanted.
    /// </para>
    /// <para>
    /// Never throws and never fails the observation. A browser that is not there, a port that never opened, a
    /// page that navigated mid-read: all of them mean "this window has no DOM elements", which is the
    /// coordinate path's window and exactly where it was before.
    /// </para>
    /// </summary>
    /// <param name="entityId">One window, or null for every window with a debug port.</param>
    /// <returns>How many elements were added — zero everywhere means nothing changed for anyone.</returns>
    public async Task<int> AddDomElementsAsync(string? entityId = null, CancellationToken ct = default)
    {
        if (_cdp is null || _cdpPorts is null || _lastObservation is null)
        {
            return 0;
        }

        var added = 0;
        var claimed = new HashSet<int>();

        foreach (var entity in _lastObservation.Entities)
        {
            if (entityId is not null && !string.Equals(entityId, entity.EntityId, StringComparison.Ordinal))
            {
                continue;
            }

            // One window per process. A browser's two tabs are two windows sharing one debug port, and the page
            // on it is whichever one answered — so a second window of the same process would be handed a copy of
            // the first one's controls at the first one's screen coordinates. That is worse than nothing: it
            // looks like a working answer and it clicks the wrong window.
            if (!claimed.Add(entity.ProcessId))
            {
                continue;
            }

            var port = _cdpPorts.PortOf(entity.ProcessId);
            if (port is not { } debugPort)
            {
                continue;
            }

            var client = _clientOrigin(ParseHandle(entity.EntityId));
            if (client.X < 0)
            {
                continue;
            }

            var page = await _cdp.ReadAsync(debugPort, entity.Label, ct).ConfigureAwait(false);
            if (page is null)
            {
                continue;
            }

            var elements = page.Elements
                .Select(record => CdpDomSource.ToElement(
                    record,
                    entity.EntityId,
                    client.X,
                    client.Y,
                    page.DevicePixelRatio))
                .ToList();

            if (elements.Count == 0)
            {
                continue;
            }

            // Only this window's own DOM elements are replaced: a fresh UI Automation walk already cleared
            // them, and adding again has to leave exactly one copy rather than accumulate.
            _lastObservation.Elements = _lastObservation.Elements
                .Where(existing => !IsDomElementOf(existing, entity.EntityId))
                .Concat(elements)
                .ToList();

            added += elements.Count;
        }

        return added;
    }

    /// <summary>Whether an element came from a page's DOM rather than from UI Automation.</summary>
    internal static bool IsDomElementOf(UiElement element, string windowId) =>
        string.Equals(element.WindowId, windowId, StringComparison.Ordinal)
        && element.ElementId.Contains(DomMarker, StringComparison.Ordinal);

    /// <summary>The part of a DOM element's id that is the page-side handle.</summary>
    private const string DomMarker = "#cdp:";

    /// <summary>
    /// Acts on one of a page's own controls. Null when this is not one — the caller then tries the UI
    /// Automation path, which is also the path that decides whether the control is reachable at all.
    /// </summary>
    public async Task<UiaInvocation?> TryActDomAsync(
        nint handle,
        string entityId,
        string elementId,
        ActionKind kind,
        CancellationToken ct)
    {
        if (_cdp is null || _cdpPorts is null || elementId is not { Length: > 0 })
        {
            return null;
        }

        // The marker is part of the id: {windowId}#cdp:{marker}. Parsed here rather than by the UI Automation
        // side, which must not learn what a browser element is.
        var marker = elementId.IndexOf("#cdp:", StringComparison.Ordinal);
        if (marker < 0 || elementId.Length == marker + DomMarker.Length)
        {
            return null;
        }

        if (kind is not (ActionKind.LeftClick or ActionKind.Focus))
        {
            return null;
        }

        var entity = _lastObservation?.Find(entityId);
        if (entity is null)
        {
            return null;
        }

        var port = _cdpPorts.PortOf(entity.ProcessId);
        if (port is not { } debugPort)
        {
            return null;
        }

        return await _cdp.ActAsync(debugPort, elementId[(marker + DomMarker.Length)..], entity.Label, ct).ConfigureAwait(false);
    }

    /// <summary>Acts on a named control through its UI Automation pattern, or null when that does not apply — the
    /// action names no control, or the control has no pattern that means what was asked. A pattern that was
    /// tried and failed is reported as a failure rather than retried as a blind coordinate click: if the
    /// control refused to be invoked, clicking its centre is not going to work either.
    /// </summary>
    private static InjectionResult? ElementPattern(
        nint handle,
        string entityId,
        ActionKind kind,
        ActionParams parameters,
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<System.Windows.Automation.AutomationElement> Nodes)? known)
    {
        if (parameters.ElementId is not { Length: > 0 } elementId)
        {
            return null;
        }

        var invocation = UiaTree.TryAct(handle, entityId, elementId, kind, known);

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
        ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll or ActionKind.DoubleClick =>
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

    /// <summary>
    /// Whether an action travels through <c>SendInput</c>, and is therefore subject to UIPI. A UI Automation
    /// pattern is not: it is a per-process RPC to the control itself, which is part of why it is the one way
    /// to reach a control this process is too low to click.
    /// </summary>
    internal static bool DeliversInput(ActionKind kind) => kind switch
    {
        // Focus does not inject: it is a window-manager call (SetForegroundWindow), and activating an
        // elevated window is exactly what a person's alt-tab does. Everything else here ends in an input
        // event, and an input event sent toward a higher-integrity window is dropped by the OS.
        ActionKind.Focus => false,
        ActionKind.ScrollIntoView => false,
        _ => true,
    };

    /// <summary>
    /// Whether the action's effect is measurable in pixels, so a before-frame is worth keeping. The aim-only
    /// kinds change where the next action goes — the pointer's position, the focused control, a control
    /// scrolled to the edge — and any repaint that follows belongs to the next action, not this one.
    /// Verifying them anyway reported "no change" as a verdict on an effect that was never the claim, and a
    /// real session then treated that verdict as its click having failed.
    /// </summary>
    internal static bool NeedsVerification(ActionKind kind) => kind
        is not (ActionKind.Focus or ActionKind.MovePointer or ActionKind.ScrollIntoView);

    /// <summary>Injected input — the path used for everything a pattern cannot express.</summary>
    private InjectionResult Inject(ActionKind kind, nint handle, ActionParams parameters) => kind switch
    {
        ActionKind.Focus => _injector.Focus(handle),
        ActionKind.MovePointer => _injector.MovePointer(parameters.X, parameters.Y),
        ActionKind.LeftClick => parameters.ClickCount == 2
            ? _injector.DoubleClick(parameters.X, parameters.Y)
            : _injector.LeftClick(parameters.X, parameters.Y),
        ActionKind.RightClick => _injector.RightClick(parameters.X, parameters.Y),
        ActionKind.KeyPress => _injector.KeyPress(parameters.VirtualKey, parameters.Modifiers),
        ActionKind.TypeText => _injector.TypeText(parameters.Text ?? string.Empty),
        ActionKind.Scroll => ScrollIn(parameters),
        ActionKind.Drag => _injector.Drag(parameters.X, parameters.Y, parameters.ToX, parameters.ToY),
        _ => new InjectionResult(
            false,
            0,
            $"{kind} cannot be performed as injected input here"
            + (kind == ActionKind.ScrollIntoView
                ? "：这个控件没有 ScrollItem/ScrollPattern（可能它并不在一个可滚动容器里）"
                : string.Empty)),
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

    /// <summary>
    /// One picture event, from either screenshot tool shape (explicit or zoom). The tuning question it
    /// feeds is "how much of the token budget goes to pictures", and the zoom flag separates the cheap
    /// whole-window picture from the deliberate second-stage crop.
    /// </summary>
    public void RecordToolPicture(string tool, string? target, int width, int height, bool zoom)
    {
        if (_telemetry is null)
        {
            return;
        }

        try
        {
            _telemetry.Record(new Telemetry.DesktopTelemetryEvent(
                _clockLocal.GetUtcNow(),
                tool,
                zoom ? "zoom" : null,
                target,
                Ok: true,
                Snapped: null,
                Verify: null,
                width,
                height,
                LatencyMs: 0,
                StopReason: null));
        }
        catch (Exception)
        {
            // Telemetry must not fail the capture that fed it.
        }
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
