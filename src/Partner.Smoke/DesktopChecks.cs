namespace Partner.Smoke;

internal static partial class Program
{
    /// <summary>
    /// Everything about a minimized window: is it reported as one, is it refused as a picture, is it refused
    /// as a click target, and can a person get it back. The last two need a real window with a message pump and
    /// a session that will grant foreground, so they are opt-in (PARTNER_DESKTOP_LIVE=1); the first three act
    /// on a window this run created.
    /// </summary>
    private static async Task MinimizedWindowChecksAsync(string root)
    {
        using (var shotAutomation = new DesktopAutomation())
        using (var owned = OwnedWindow.Create("Partner smoke — minimized window probe"))
        {
            var probe = owned.Created
                ? shotAutomation.Observe().Entities.FirstOrDefault(entity => entity.Label.Contains("Partner smoke", StringComparison.Ordinal))
                : null;

            if (probe is null)
            {
                var why = owned.Failure ?? "the probe window did not appear in the observation";
                Skip("desktop: a minimized window is reported as minimized rather than as a small window", why);
                Skip("desktop: a minimized window is refused as a picture instead of a 160×28 stub", why);
                Skip("desktop: a click into a minimized window's placeholder rect is refused", why);
            }
            else
            {
                var handle = nint.Parse(probe.EntityId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                WindowProbe.Minimize(handle);
                await Task.Delay(700);

                var minimized = shotAutomation.Observe().Find(probe.EntityId);

                Check("desktop: a minimized window is reported as minimized rather than as a small window",
                    minimized is { IsMinimized: true },
                    $"minimized={minimized?.IsMinimized} rect={minimized?.Rect.X},{minimized?.Rect.Y} {minimized?.Rect.Width}×{minimized?.Rect.Height}");

                if (minimized is not null)
                {
                    var refused = await new DesktopScreenshotTool(shotAutomation, new FileMediaStore(Path.Combine(root, "media")))
                        .InvokeAsync($"{{\"entityId\":\"{minimized.EntityId}\"}}");

                    Check("desktop: a minimized window is refused as a picture instead of a 160×28 stub",
                        !refused.Ok && refused.Content.Contains("已最小化", StringComparison.Ordinal),
                        refused.Content);

                    var blocked = DesktopActTool.Guard(
                        ActionKind.LeftClick,
                        minimized,
                        (minimized.Rect.X + 5, minimized.Rect.Y + 5),
                        new DesktopActTool.DesktopActArguments(
                            null, null, "LeftClick", minimized.Rect.X + 5, minimized.Rect.Y + 5, null, null, null, null, null));

                    Check("desktop: a click into a minimized window's placeholder rect is refused",
                        blocked is not null && blocked.Contains("已最小化", StringComparison.Ordinal),
                        blocked ?? "(none)");
                }
            }

            if (!LiveDesktop)
            {
                Skip("desktop: focusing a minimized window restores it instead of only lighting up the taskbar",
                    "PARTNER_DESKTOP_LIVE is not 1 (focusing a window takes the reader's focus)");
                Skip("desktop: a restored window is screenshot from the stale observation instead of being refused",
                    "PARTNER_DESKTOP_LIVE is not 1 (needs the focus check above, and a window with a message pump)");
            }
            else
            {
                // A real window, and never the shell's own: Program Manager cannot be minimized, so the check
                // would then be about nothing. The focus is handed back at the end.
                var all = shotAutomation.Observe().Entities;
                var original = all.FirstOrDefault(entity => entity.IsForeground && entity.Label != "Program Manager");
                var live = original ?? all.FirstOrDefault(entity => !entity.IsMinimized && entity.Label != "Program Manager");

                if (live is null)
                {
                    var why = "no window on this desktop can be minimized";
                    Skip("desktop: focusing a minimized window restores it instead of only lighting up the taskbar", why);
                    Skip("desktop: a restored window is screenshot from the stale observation instead of being refused", why);
                }
                else
                {
                    var liveHandle = nint.Parse(live.EntityId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                    WindowProbe.Minimize(liveHandle);
                    await Task.Delay(700);

                    var stale = shotAutomation.Observe().Find(live.EntityId);

                    if (stale is null)
                    {
                        var why = $"'{live.Label}' vanished after being minimized";
                        Skip("desktop: focusing a minimized window restores it instead of only lighting up the taskbar", why);
                        Skip("desktop: a restored window is screenshot from the stale observation instead of being refused", why);
                    }
                    else
                    {
                        // Focus is what a person does to a minimized window, and it has to mean the same thing here.
                        var focused = await shotAutomation.ActAsync(stale.EntityId, ActionKind.Focus, new ActionParams());
                        await Task.Delay(700);

                        var restored = shotAutomation.Observe().Find(stale.EntityId);

                        Check("desktop: focusing a minimized window restores it instead of only lighting up the taskbar",
                            focused.Injection?.Posted == true && restored is { IsMinimized: false } && restored.Rect.Y > -32000,
                            $"injection={focused.Injection?.Detail} win32={focused.Injection?.ErrorCode} minimized={restored?.IsMinimized} rect={restored?.Rect.X},{restored?.Rect.Y}");

                        // The picture must not be refused from the observation taken *before* the focus: a
                        // restore that already happened would look like it had not. The tool has to ask the
                        // window.
                        var afterRestore = await new DesktopScreenshotTool(shotAutomation, new FileMediaStore(Path.Combine(root, "media")))
                            .InvokeAsync($"{{\"entityId\":\"{stale.EntityId}\"}}");

                        Check("desktop: a restored window is screenshot from the stale observation instead of being refused",
                            afterRestore.Ok && afterRestore.Images is { Count: 1 },
                            afterRestore.Content[..Math.Min(80, afterRestore.Content.Length)]);

                        if (original is not null)
                        {
                            await shotAutomation.ActAsync(original.EntityId, ActionKind.Focus, new ActionParams());
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Titles of the probe windows the lease checks own, one per scenario. They do not contain "Partner smoke",
    /// the text the minimized-window checks search for.
    /// </summary>
    private const string LeaseProbeTitle = "Partner lease probe — smoke";

    private const string GrantProbeTitle = "Partner grant probe — smoke";

    private static class WindowProbe
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint handle, int command);

        /// <summary>SW_MINIMIZE.</summary>
        public static void Minimize(nint handle) => ShowWindow(handle, 6);
    }

    /// <summary>
    /// A window the smoke run owns, so the minimized-window checks never touch the reader's own.
    /// <para>
    /// The state has to be produced — nothing in the product minimizes a window — and it must be produced on
    /// a window the run owns: minimizing whatever was in front makes the result a property of the machine
    /// rather than of the code, because a fullscreen window takes the focus back the moment it is minimized.
    /// </para>
    /// <para>
    /// It lives on a thread of its own with a real message pump, and both halves of that are load-bearing.
    /// Without the pump, capturing the window kills it — a GDI capture sends messages to the window's own
    /// thread and nobody is there to answer, so what comes back is a dead handle rather than pixels. Without
    /// the dedicated thread, the window belongs to whichever thread happened to call in, which here is a
    /// thread-pool thread: the runtime retires those, and it takes every window they created with it, at a
    /// moment of its choosing.
    /// </para>
    /// </summary>
    private sealed class OwnedWindow : IDisposable
    {
        private const string ClassName = "PartnerSmokeProbeWindow";
        private const uint WmClose = 0x0010;
        private const uint WmQuit = 0x0012;
        private const uint ErrorClassAlreadyExists = 1410;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")]
        private static extern ushort RegisterClass(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern nint CreateWindowExW(
            uint extended, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint handle);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint handle, int command);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandleW(string? module);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostThreadMessageW")]
        private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            // WNDCLASSW, not WNDCLASSEXW: RegisterClass (not RegisterClassEx) takes no cbSize and no atom,
            // and getting that layout wrong is ERROR_INVALID_PARAMETER rather than anything readable.
            public uint Style;
            public nint WindowProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public nint Window;
            public uint Value;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public int PointX;
            public int PointY;
        }

        /// <summary>The thread's message pump, kept as a delegate so the window's procedure stays callable.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);

        private static readonly WindowProcedure Procedure = DefWindowProc;

        private readonly ManualResetEventSlim _ready = new(false);
        private Thread? _thread;

        public nint Handle { get; private set; }

        /// <summary>False when the session will not hand this run a window (a locked-down desktop returns none).</summary>
        public bool Created { get; private set; }

        /// <summary>Why the window is absent, when it is — a skip nobody can act on is a skip that gets ignored.</summary>
        public string? Failure { get; private set; }

        /// <summary>
        /// A visible, ordinary, titled window: big enough that Windows gives it a real placement rather than
        /// the 160×28 taskbar stub, and empty so there is nothing to look at.
        /// </summary>
        /// <param name="showWithoutActivating">True to show the window without activating it, so it is never in front.</param>
        public static OwnedWindow Create(string title, bool showWithoutActivating = false, bool addButton = false)
        {
            var window = new OwnedWindow();
            nint handle = 0;
            string? failure = null;

            var thread = new Thread(() =>
            {
                var instance = GetModuleHandleW(null);
                var windowClass = new WindowClass
                {
                    Name = ClassName,
                    WindowProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                    Instance = instance,
                    // A plain button face: an empty window is fine, an invisible one would not be findable.
                    Background = 5,
                };

                if (RegisterClass(ref windowClass) == 0 && GetLastError() != ErrorClassAlreadyExists)
                {
                    failure = $"RegisterClassW failed (win32 {GetLastError()})";
                    window._ready.Set();
                    return;
                }

                // WS_OVERLAPPEDWINDOW, plus WS_VISIBLE unless the caller shows the window itself.
                var style = showWithoutActivating ? 0x00CF0000u : 0x00CF0000u | 0x10000000u;
                handle = CreateWindowExW(
                    0,
                    ClassName,
                    title,
                    style,
                    120,
                    120,
                    480,
                    240,
                    0,
                    0,
                    instance,
                    0);
                if (handle == 0)
                {
                    failure = $"CreateWindowExW failed (win32 {GetLastError()})";
                    window._ready.Set();
                    return;
                }

                if (addButton && CreateWindowExW(0, "BUTTON", "Smoke button", 0x50010000u,
                    20, 20, 120, 30, handle, 0, instance, 0) == 0)
                {
                    failure = $"CreateWindowExW button failed (win32 {GetLastError()})";
                    DestroyWindow(handle);
                    handle = 0;
                    window._ready.Set();
                    return;
                }

                if (showWithoutActivating)
                {
                    // SW_SHOWNOACTIVATE: show without taking focus.
                    ShowWindow(handle, 4);
                }

                window._ready.Set();
                Pump(handle);
            })
            {
                IsBackground = true,
                Name = "partner-smoke-window",
            };

            window._thread = thread;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // Bounded wait for the window thread.
            if (!window._ready.Wait(TimeSpan.FromSeconds(15)))
            {
                failure ??= "the window thread did not answer";
            }

            window.Handle = handle;
            window.Created = handle != 0;
            window.Failure = failure;
            return window;
        }

        /// <summary>
        /// The pump. A close is how this run ends the window; anything else goes to DefWindowProc, which is
        /// all a bare window needs. It exits on WM_QUIT, which <see cref="Dispose"/> posts by thread id.
        /// </summary>
        private static void Pump(nint window)
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Value == WmClose)
                {
                    DestroyWindow(message.Window);
                    continue;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(ref NativeMessage message);

        public void Dispose()
        {
            if (Handle != 0)
            {
                PostMessage(Handle, WmClose, 0, 0);
            }

            // WM_QUIT is thread-directed, so it cannot ride along on the window's queue.
            if (_thread is not null)
            {
                PostThreadMessage((uint)_thread.ManagedThreadId, WmQuit, 0, 0);
                _thread.Join(TimeSpan.FromSeconds(2));
            }

            _ready.Dispose();
        }
    }
    private static async Task DesktopChecksAsync(string root)
    {
        var windows = WindowEnumerator.List();
        Check("desktop: window enumeration finds visible windows",
            windows.Count > 0 && windows.All(w => w.Handle != 0 && w.Width > 0 && w.Height > 0),
            $"windows={windows.Count}");

        // The point a click is delivered to is decided by the OS, not by the rectangle a caller named. This is
        // the read-only half of that rule: the hit test must resolve to a real top-level window (a child
        // handle would make the comparison against an observed window meaningless) and that window must
        // contain the point (a virtualized coordinate would land somewhere else entirely).
        var probeWindow = windows.FirstOrDefault(w => w.Width >= 200 && w.Height >= 200);
        if (probeWindow is null)
        {
            Check("desktop: the window under a point is the top-level window that contains it", false, "no window large enough to probe");
        }
        else
        {
            var probeX = probeWindow.X + probeWindow.Width / 2;
            var probeY = probeWindow.Y + probeWindow.Height / 2;
            var under = HitTest.At(probeX, probeY);

            var contains = false;
            var isRoot = false;
            if (under.Handle != 0)
            {
                var bounds = WindowEnumerator.GetBounds(under.Handle);
                contains = probeX >= bounds.Left && probeX < bounds.Right
                    && probeY >= bounds.Top && probeY < bounds.Bottom;
                isRoot = NativeMethods.GetAncestor(under.Handle, NativeMethods.GaRoot) == under.Handle;
            }

            Check("desktop: the window under a point is the top-level window that contains it",
                under.Handle != 0 && isRoot && contains,
                $"({probeX},{probeY}) -> {under.Handle:x} '{under.Title}' contains={contains} root={isRoot}");
        }

        var captureTarget = windows.FirstOrDefault(w => w.Width >= 100 && w.Height >= 100);
        if (captureTarget is null)
        {
            Check("desktop: GDI capture returns pixels", false, "no window large enough to capture");
        }
        else
        {
            var capture = new GdiScreenCapture();
            var frame = await capture.CaptureWindowAsync(captureTarget.Handle);
            Check("desktop: GDI capture returns pixels",
                frame.Ok && frame.Bgra.Length == frame.Width * frame.Height * 4 && frame.Method.StartsWith("gdi", StringComparison.Ordinal),
                $"ok={frame.Ok} {frame.Width}x{frame.Height} method={frame.Method} err={frame.Error}");

            // Real end-to-end verification against two live captures — no input injected.
            var verifier = new PostActionVerifier(capture);
            var verify = await verifier.VerifyAsync(
                captureTarget.Handle,
                new PixelRect(captureTarget.X, captureTarget.Y, captureTarget.Width, captureTarget.Height),
                frame);

            Check("desktop: post-action verification measures rather than assuming",
                verify.Decision != VerifyDecision.Indeterminate,
                $"decision={verify.Decision} ratio={verify.IntersectedAreaRatio:P2} elapsed={verify.ElapsedMs:0.0}ms "
                + $"budget={verifier.Budget.TotalMilliseconds:0}ms");
        }

        using (var automation = new DesktopAutomation())
        {
            var observation = automation.Observe();
            Check("desktop: lease is issued per entity with a unique generation",
                observation.Entities.Count > 0
                && observation.Entities.All(e => e.Generation == observation.ObsId)
                && automation.Leases.Count == observation.Entities.Count,
                $"entities={observation.Entities.Count} leases={automation.Leases.Count}");

            var refused = await automation.ActAsync(
                "deadbeef", ActionKind.MovePointer, new ActionParams { X = 1, Y = 1 });
            Check("desktop: action on an unknown entity is refused without injecting",
                !refused.GuardPassed && refused.GuardReason == "entity_not_found" && refused.Injection is null,
                refused.GuardReason ?? "(none)");

            // A named control is acted on through its UI Automation pattern when it has one. What can be
            // checked without touching anything live is the routing decision: an action no pattern can express
            // must come back as "not applicable", which is what tells the caller to inject at the element's
            // centre. A non-null here would mean the action was silently swallowed.
            var sample = observation.Elements.FirstOrDefault();
            var handle = sample is null
                ? 0
                : nint.Parse(sample.WindowId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            var routedToInput = sample is not null
                && new[]
                {
                    ActionKind.MovePointer, ActionKind.RightClick, ActionKind.Scroll,
                    ActionKind.TypeText, ActionKind.KeyPress, ActionKind.Drag,
                }.All(kind => UiaTree.TryAct(handle, sample.WindowId, sample.ElementId, kind) is null);

            Check("desktop: an action a pattern cannot express is routed to injected input",
                routedToInput,
                sample is null
                    ? "no UIA elements in this observation"
                    : $"'{sample.Name}' [{sample.Role}]");

            // An id is an index into one observation, so it stops resolving as soon as the tree changes.
            // Substituting "whatever is at that spot now" is how an action lands on the wrong control.
            var notGuessed = sample is not null
                && UiaTree.TryAct(handle, sample.WindowId, $"{sample.WindowId}:9999", ActionKind.LeftClick) is null
                && UiaTree.TryAct(handle, sample.WindowId, "not-an-element-id", ActionKind.LeftClick) is null;

            Check("desktop: a control id that does not resolve is not guessed at",
                notGuessed,
                sample is null ? "no UIA elements in this observation" : sample.ElementId);

            // What goes on the picture the model reasons about. Estimating a position from an unmarked image
            // is unreliable. A mark is not an estimate: it names a control the observation found and carries
            // that control's own click point.
            var sampleWindow = sample is null ? null : observation.Find(sample.WindowId);

            var marks = sampleWindow is null
                ? Array.Empty<ScreenshotMark>()
                : ScreenshotMarks.Marks(
                    observation.Elements
                        .Where(element => string.Equals(element.WindowId, sampleWindow.EntityId, StringComparison.Ordinal))
                        .ToList(),
                    sampleWindow.Rect);

            var marksCarryRealPoints = marks.Count > 0
                && marks.Select((mark, position) => mark.Index == position + 1).All(numbered => numbered)
                && marks.All(mark => observation.Elements.Any(element =>
                    string.Equals(element.ElementId, mark.ElementId, StringComparison.Ordinal)
                    && element.Centre == mark.ClickAt));

            Check("desktop: a picture's marks carry each control's real screen point",
                marksCarryRealPoints,
                marks.Count == 0
                    ? "no marks for the sampled window"
                    : $"{marks.Count} marks, first = '{marks[0].Label}' @ ({marks[0].ClickAt.X},{marks[0].ClickAt.Y})");
        }

        var desktopClock = new OffsetClock(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        using (var expiring = new DesktopAutomation(
                   ttlPolicy: new LeaseTtlPolicy { BaseMs = 1_000, FloorMs = 1_000 },
                   clock: desktopClock))
        {
            var observation = expiring.Observe();
            var entity = observation.Entities.First();
            desktopClock.Advance(TimeSpan.FromMinutes(5));

            var refused = await expiring.ActAsync(entity.EntityId, ActionKind.MovePointer, new ActionParams());
            Check("desktop: an expired lease refuses the action without injecting",
                !refused.GuardPassed && refused.GuardReason == "Expired" && refused.Injection is null,
                refused.GuardReason ?? "(none)");
        }

        // The action's answer carries the state after it: the same pipeline the screenshot tool runs,
        // attached to a result the model was already about to read. Two read-only evidences — when a picture
        // is promised (the decision matrix, which must exclude a refusal, the aim-only kinds and a caller who
        // said off) and what one render yields (a real PNG under a media id the server's px/py mapping can
        // resolve). Both drive a stub capture, so the user's screen stays out of the check.
        {
            var matrix = DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.LeftClick, "off", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: false, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: true, guardPassed: false)
                && !DesktopActTool.WantsPicture(ActionKind.Focus, "auto", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.MovePointer, "auto", supportsImages: true, guardPassed: true)
                && !DesktopActTool.WantsPicture(ActionKind.ScrollIntoView, "auto", supportsImages: true, guardPassed: true);

            Check("desktop: an act answer promises a picture for the kinds that change what is seen",
                matrix
                && new DesktopActTool(
                    new DesktopAutomation(), DesktopToolPolicy.Any, new FileMediaStore(root),
                    supportsImages: true).Spec.Description.Contains("feedback=auto", StringComparison.Ordinal)
                && new DesktopActTool(
                    new DesktopAutomation(), DesktopToolPolicy.Any, new FileMediaStore(root),
                    supportsImages: true).Spec.ParametersJsonSchema.Contains("feedback", StringComparison.Ordinal),
                matrix ? "matrix holds and the schema advertises it" : "decision matrix broke");

            var workspace = Path.Combine(root, "desktop-act-state");
            Directory.CreateDirectory(workspace);
            const int frameWidth = 320;
            const int frameHeight = 200;
            var frameRect = new PixelRect(40, 30, frameWidth, frameHeight);
            using (var automation = new DesktopAutomation(capture: new PositionEchoCapture(frameRect)))
            {
                var target = new UiEntity
                {
                    EntityId = "1a2b",
                    Generation = "obs-x",
                    Role = "window",
                    Label = "smoke 窗口",
                    Value = "smoke",
                    Rect = frameRect,
                    Confidence = 1.0,
                    IdentityDigest = "digest-x",
                    LayoutDigest = DesktopObservation.Layout(frameRect),
                    ProcessId = 4242,
                    ObservedAtMs = desktopClock.GetUtcNow(),
                };

                var picture = await DesktopScreenshotTool.RenderAfterActionAsync(
                    automation, new FileMediaStore(workspace), target,
                    new List<UiElement> { new("e-1", "1a2b", "播放", "Button", new PixelRect(50, 40, 80, 30), "element-a") },
                    obsId: "obs-x",
                    ct: default);

                var mapped = automation.PictureOf(picture?.MediaId ?? "");
                Check("desktop: an act answer's attached picture maps back and is recorded by id",
                    picture is not null
                    && mapped is not null
                    && frameRect == mapped.ScreenRect
                    && frameWidth == mapped.PictureWidth
                    && frameHeight == mapped.PictureHeight
                    && "obs-x" == mapped.ObsId
                    && picture.DataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                    && automation.LastShownEntityId == target.EntityId,
                    picture is null
                        ? "render produced nothing"
                        : $"{picture.Width}x{picture.Height} media={picture.MediaId} marks={picture.Marks.Count}");
            }
        }

        // The step budget is keyed by tool family: a GUI task runs 20–40 look-act-look steps, so a single
        // flat number would run out on the first two controls. Keyed on what the registry holds, so the same
        // turn runner gives a plain chat turn only the base number.
        {
            var desktopFamily = new InMemoryToolRegistry();
            desktopFamily.Register(new StubTool("desktop_act", "done"));
            desktopFamily.Register(new StubTool("desktop_observe", "seen"));

            var plainFamily = new InMemoryToolRegistry();
            plainFamily.Register(new StubTool("clock", "12:34"));

            var options = new AgentTurnOptions();
            var desktopBudget = options.StepsAllowedFor(desktopFamily.Specs.Select(spec => spec.Name));
            var plainBudget = options.StepsAllowedFor(plainFamily.Specs.Select(spec => spec.Name));

            Check("desktop: the step budget is measured by tool family, not one number",
                options.MaxSteps == 8
                && desktopBudget > 8
                && plainBudget == 8,
                $"default={options.MaxSteps} desktop={desktopBudget} plain={plainBudget}");
        }

        // Waiting is one call with a deadline instead of the observe-observe loop. The wait for a control
        // that is already there answers at once with a fresh id (a real aimed walk, read-only).
        using (var waitAutomation = new DesktopAutomation())
        {
            var observation = waitAutomation.Observe();
            var target = observation.Entities.FirstOrDefault(entity => !entity.IsMinimized);
            var element = observation.Elements.FirstOrDefault(candidate => candidate.WindowId == target!.EntityId);
            if (target is not null && element is not null)
            {
                var waitTool = new DesktopWaitTool(waitAutomation);
                var found = await waitTool.InvokeAsync($$"""
                    {"kind":"element","entityId":"{{target.EntityId}}","elementId":"{{element.ElementId}}"}
                    """);

                Check("desktop: a wait for a control that is already there answers at once with the new id",
                    found.Ok && found.Content.Contains("控件已出现", StringComparison.Ordinal)
                        && found.Content.Contains("desktop_act 可直接用", StringComparison.Ordinal),
                    found.Content[..Math.Min(70, found.Content.Length)]);
            }
        }

        // The taskbar/tray/notification don't belong to any top-level window, so they never appear in
        // observations; the full-frame screenshot brings them in along with the window marks, with no
        // prerequisite observation.
        using (var scopeAutomation = new DesktopAutomation())
        {
            var workspace = Path.Combine(root, "desktop-scope");
            Directory.CreateDirectory(workspace);
            var scopeTool = new DesktopScreenshotTool(scopeAutomation, new FileMediaStore(workspace));

            var shot = await scopeTool.InvokeAsync("""{"scope":"desktop","maxDimension":4096}""");
            var mediaId = System.Text.RegularExpressions.Regex.Match(
                shot.Content, "媒体 ([0-9a-f]{8,})").Groups[1].Value;
            var mapped = string.IsNullOrEmpty(mediaId) ? null : scopeAutomation.PictureOf(mediaId);
            var expectations = VirtualDesktop.Current();

            Check("desktop: a full-desktop picture exists without any observation and is recorded as desktop",
                shot.Ok
                && shot.Images is { Count: > 0 }
                && mapped is not null
                && mapped.EntityId == "desktop"
                && mapped.ScreenRect == new PixelRect(expectations.X, expectations.Y, expectations.Width, expectations.Height),
                shot.Ok ? $"media={mediaId} marks=?" : shot.Content[..Math.Min(70, shot.Content.Length)]);

            // px/py on a full-frame image resolves to a bare screen coordinate (forwarded to the input-free
            // refusal path). Pick a non-foreground window centre; skip when no unambiguous window is found
            // (fullscreen or foreground lockup).
            var fresh = scopeAutomation.Observe();
            var background = fresh.Entities.FirstOrDefault(entity =>
                !entity.IsForeground && !entity.IsMinimized && entity.Rect.Width > 100);
            if (background is not null && mapped is not null)
            {
                var centreX = background.Rect.X + background.Rect.Width / 2;
                var centreY = background.Rect.Y + background.Rect.Height / 2;
                var px = (int)Math.Round((centreX - mapped.ScreenRect.X) * mapped.PictureWidth / (double)mapped.ScreenRect.Width);
                var py = (int)Math.Round((centreY - mapped.ScreenRect.Y) * mapped.PictureHeight / (double)mapped.ScreenRect.Height);

                // The expected point: the window centre mapped the way the tool maps it.
                var expected = DesktopActTool.MapPicturePoint(mapped, px, py);

                var actTool = new DesktopActTool(
                    scopeAutomation, DesktopToolPolicy.Any, new FileMediaStore(workspace));
                var resolved = await actTool.InvokeAsync(
                    $$"""
                    {"kind":"KeyPress","virtualKey":67,"px":{{px}},"py":{{py}},"mediaId":"{{mediaId}}","feedback":"off"}
                    """);

                // The point is the contract; which guard then refuses it depends on what is on screen
                // (foreground lockup, overlapping windows, policy), so that part is not pinned.
                Check("desktop: px/py on the desktop picture resolves to the point it was taken from",
                    !resolved.Ok
                    && resolved.Content.Contains($"({expected.X},{expected.Y})", StringComparison.Ordinal),
                    $"expected=({expected.X},{expected.Y}) ok={resolved.Ok} "
                        + resolved.Content[..Math.Min(100, resolved.Content.Length)]);
            }
            else
            {
                // A clean desktop, or one whose only window is in front, has no point to resolve on. Verify the
                // same arithmetic on a synthetic picture rather than letting the capability drop out of the gate
                // because of the machine it ran on.
                var synthetic = new ShownPicture("desktop", new PixelRect(0, 0, 2560, 1440), 1280, 720, "obs-synthetic");
                var mappedPoint = DesktopActTool.MapPicturePoint(synthetic, 640, 360);

                Check("desktop: px/py on the desktop picture resolves to the point it was taken from",
                    mappedPoint == (1280, 720),
                    $"expected=(1280,720) got=({mappedPoint.X},{mappedPoint.Y})");
            }
        }

        // px/py snap fallback: the proof path is safe because a key aimed at a background window is refused
        // by the guard, so nothing is injected; the refusal names the resolved control, which is sufficient
        // evidence.
        using (var snapAutomation = new DesktopAutomation())
        {
            var workspace = Path.Combine(root, "desktop-snap");
            Directory.CreateDirectory(workspace);
            var observation = snapAutomation.Observe();
            var target = observation.Entities.FirstOrDefault(entity =>
                !entity.IsForeground && !entity.IsMinimized && entity.Rect.Width > 100);
            var element = target is null
                ? null
                : observation.Elements.FirstOrDefault(candidate =>
                    candidate.WindowId == target.EntityId
                    && !candidate.IsOffscreen
                    && candidate.Role == "Button"
                    && !string.IsNullOrEmpty(candidate.Name)
                    && candidate.Rect.Width > 10);
            if (target is not null && element is not null)
            {
                var shotTool = new DesktopScreenshotTool(snapAutomation, new FileMediaStore(workspace));
                var shot = await shotTool.InvokeAsync(
                    $$"""{"entityId":"{{target.EntityId}}","maxDimension":4096}""");
                var mediaId = System.Text.RegularExpressions.Regex.Match(
                    shot.Content, "媒体 ([0-9a-f]{8,})").Groups[1].Value;
                var picture = snapAutomation.PictureOf(mediaId);

                // Deliberately 5px off: lands inside the control's snap neighbourhood (8px radius); the
                // server absorbs a one-step error from the picture.
                var px = (int)Math.Round((element!.Centre.X + 5 - picture!.ScreenRect.X) * picture.PictureWidth / (double)picture.ScreenRect.Width);
                var py = (int)Math.Round((element.Centre.Y - picture.ScreenRect.Y) * picture.PictureHeight / (double)picture.ScreenRect.Height);

                var snapAct = new DesktopActTool(
                    snapAutomation, DesktopToolPolicy.Any, new FileMediaStore(workspace));
                var resolved = await snapAct.InvokeAsync(
                    $$"""{"kind":"KeyPress","virtualKey":67,"px":{{px}},"py":{{py}},"mediaId":"{{mediaId}}","feedback":"off"}""");

                Check("desktop: a px/py miss within the snap radius resolves to the control it was for",
                    !resolved.Ok
                    && resolved.Content.Contains($"'{element.Name}' [{element.Role}]", StringComparison.Ordinal),
                    resolved.Content[..Math.Min(90, resolved.Content.Length)]);
            }
            else
            {
                // No background window with a named button (a clean desktop, or one UIA cannot see). Verify the
                // same snap arithmetic on a synthetic observation rather than letting the capability drop out
                // of the gate because of the machine it ran on.
                var frame = new PixelRect(0, 0, 400, 300);
                var syntheticElement = new UiElement(
                    "w:1", "w", "OK", "Button", new PixelRect(100, 100, 60, 30), "digest");
                var synthetic = new DesktopObservation
                {
                    ObsId = "snap",
                    TargetKey = "snap",
                    Timestamp = DateTimeOffset.UnixEpoch,
                    Sources = Array.Empty<string>(),
                    Entities = new[]
                    {
                        new UiEntity
                        {
                            EntityId = "w",
                            Generation = "obs-snap",
                            Role = "window",
                            Label = "snap",
                            Rect = frame,
                            Confidence = 1.0,
                            IdentityDigest = "digest-w",
                            LayoutDigest = DesktopObservation.Layout(frame),
                            ProcessId = 1,
                            ObservedAtMs = DateTimeOffset.UnixEpoch,
                        },
                    },
                    Elements = new[] { syntheticElement },
                };

                var hit = DesktopActTool.SnapToElement(
                    synthetic,
                    "w",
                    (syntheticElement.Centre.X + 5, syntheticElement.Centre.Y));

                Check("desktop: a px/py miss within the snap radius resolves to the control it was for",
                    hit?.ElementId == syntheticElement.ElementId,
                    hit is null ? "(no snap target)" : hit.ElementId);
            }
        }

        Check("desktop: input marshalling matches the Win32 contract",
            InputInjector.InputSize == (IntPtr.Size == 8 ? 40 : 28),
            $"INPUT size = {InputInjector.InputSize}");

        // Positional mouse events must carry MOUSEEVENTF_ABSOLUTE and MOUSEEVENTF_VIRTUALDESK; without them a
        // screen coordinate is applied as a relative delta and the click lands in a corner. Asserted on the
        // built records, because the smoke must not move the user's pointer.
        var pointerProbe = new InputInjector(() => new VirtualDesktop(0, 0, 2560, 1440));
        var positioned = pointerProbe.TryBuildPointer(1280, 720, 0, out var pointerRecords, out _);
        var positionalOk = positioned
            && (pointerRecords[0].Union.Mouse.Flags & NativeMethods.MouseeventfAbsolute) != 0
            && (pointerRecords[0].Union.Mouse.Flags & NativeMethods.MouseeventfVirtualdesk) != 0
            && pointerRecords[0].Union.Mouse.Dx is > 32000 and < 33600
            && pointerRecords[0].Union.Mouse.Dy is > 32000 and < 33600;

        Check("desktop: positional input is absolute in the 0..65535 virtual-desktop space",
            positionalOk,
            positioned
                ? $"dx={pointerRecords[0].Union.Mouse.Dx} dy={pointerRecords[0].Union.Mouse.Dy} "
                    + $"flags=0x{pointerRecords[0].Union.Mouse.Flags:x}"
                : "no records were built");

        // Clamping silently would turn "click the Save button" into "click the screen edge".
        var offScreenRefused = !pointerProbe.TryBuildPointer(99999, 720, 0, out _, out var offScreenError)
            && offScreenError?.Contains("虚拟桌面", StringComparison.Ordinal) == true;

        Check("desktop: a point outside the virtual desktop is refused instead of clamped",
            offScreenRefused,
            offScreenError ?? "(no error reported)");

    }
}
