using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Partner.Core.Media;
using Partner.Desktop;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Partner.Desktop.Tools;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;

/// <summary>
/// The end-to-end proof, on a real desktop: the pointer actually lands on the pixel it was given, and a named
/// control is actually pressed through its UI Automation pattern.
/// <para>
/// Opt-in on purpose (<c>PARTNER_DESKTOP_LIVE=1</c>), because it moves the user's cursor and shows a window —
/// neither belongs in a routine test run. The coordinate <i>contract</i> is covered without any of this by
/// <see cref="DesktopInputCoordinateTests"/>; what these add is the part only a live desktop can answer.
/// </para>
/// </summary>
public sealed class DesktopLiveInteractionTests
{
    /// <summary>Runs only when <c>PARTNER_DESKTOP_LIVE=1</c>. Reports as skipped otherwise, never as passed.</summary>
    private sealed class LiveDesktopFactAttribute : FactAttribute
    {
        public LiveDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run "
                    + "(it moves the pointer, and one of them shows a window)";
            }
        }
    }

    [LiveDesktopFact]
    public void The_pointer_lands_on_the_screen_point_it_was_given()
    {
        var injector = new InputInjector();
        var desktop = VirtualDesktop.Current();
        Assert.True(desktop.Width > 1 && desktop.Height > 1, $"no interactive desktop: {desktop}");

        GetCursorPos(out var start);

        try
        {
            var targetX = desktop.X + desktop.Width / 2 + 40;
            var targetY = desktop.Y + desktop.Height / 2 - 60;

            var moved = injector.MovePointer(targetX, targetY);
            Assert.True(moved.Ok, moved.Detail);

            GetCursorPos(out var landed);

            // This is the assertion that would have failed before the fix: without MOUSEEVENTF_ABSOLUTE the
            // coordinate was applied as a delta, so the pointer went to a screen corner instead.
            Assert.InRange(landed.X, targetX - 1, targetX + 1);
            Assert.InRange(landed.Y, targetY - 1, targetY + 1);

            // A second, unrelated point: an accumulated delta would drift, a position does not.
            var secondX = desktop.X + desktop.Width / 4;
            var secondY = desktop.Y + desktop.Height / 3;

            Assert.True(injector.MovePointer(secondX, secondY).Ok);
            GetCursorPos(out var second);

            Assert.InRange(second.X, secondX - 1, secondX + 1);
            Assert.InRange(second.Y, secondY - 1, secondY + 1);
        }
        finally
        {
            SetCursorPos(start.X, start.Y);
        }
    }

    [LiveDesktopFact]
    public void The_pointer_lands_on_the_exact_pixel_across_the_whole_width()
    {
        // The exactness the normalization fix bought, checked against the real cursor rather than against
        // the formula. The columns sampled are the ones the previous scaling was measured to put on their
        // left-hand neighbour (2, 4, 7, 9, 12, 17, 22, 27 on a 2560-wide desktop), plus a coarse sweep and
        // both edges. Before the fix these landed one pixel short and the injection still reported success.
        var injector = new InputInjector();
        var desktop = VirtualDesktop.Current();
        Assert.True(desktop.Width > 1 && desktop.Height > 1, $"no interactive desktop: {desktop}");

        var fixedY = desktop.Y + desktop.Height / 2;

        var targets = new List<int>();
        for (var pixel = 0; pixel < Math.Min(desktop.Width, 400); pixel++)
        {
            targets.Add(desktop.X + pixel);
        }

        targets.AddRange(new[] { 2, 4, 7, 9, 12, 17, 22, 27 }.Where(p => p < desktop.Width).Select(p => desktop.X + p));
        targets.Add(desktop.X);
        targets.Add(desktop.X + desktop.Width - 1);

        for (var i = 0; i <= 20; i++)
        {
            targets.Add(desktop.X + (int)Math.Round(i * (desktop.Width - 1) / 20.0));
        }

        GetCursorPos(out var start);

        try
        {
            var wrong = new List<string>();

            foreach (var target in targets.Distinct())
            {
                Assert.True(injector.MovePointer(target, fixedY).Ok);
                GetCursorPos(out var landed);

                if (landed.X != target)
                {
                    wrong.Add($"asked {target}, landed {landed.X}");
                }
            }

            Assert.Empty(wrong);
        }
        finally
        {
            SetCursorPos(start.X, start.Y);
        }
    }

    [LiveDesktopFact]
    public void A_point_off_the_desktop_is_refused_without_moving_the_pointer()
    {
        var injector = new InputInjector();
        var desktop = VirtualDesktop.Current();
        GetCursorPos(out var start);

        try
        {
            var refused = injector.MovePointer(desktop.X + desktop.Width + 500, desktop.Y + 10);

            Assert.False(refused.Ok);
            Assert.Contains("虚拟桌面", refused.Detail, StringComparison.Ordinal);

            // Refused before the call reached SendInput, so the pointer has not moved at all.
            GetCursorPos(out var after);
            Assert.Equal((start.X, start.Y), (after.X, after.Y));
        }
        finally
        {
            SetCursorPos(start.X, start.Y);
        }
    }

    [LiveDesktopFact]
    public void A_named_button_is_pressed_through_its_pattern_without_injecting_input()
    {
        using var host = new ButtonWindow();
        using var automation = new DesktopAutomation();

        var observation = automation.Observe();

        var window = observation.Find(host.HandleHex);
        Assert.NotNull(window);

        var button = observation.Elements.FirstOrDefault(element =>
            element.WindowId == host.HandleHex && element.Name == "提交");

        Assert.NotNull(button);

        GetCursorPos(out var cursorBefore);

        var invocation = UiaTree.TryAct(host.Handle, host.HandleHex, button!.ElementId, ActionKind.LeftClick);

        Assert.NotNull(invocation);
        Assert.True(invocation!.Ok, invocation.Detail);
        Assert.StartsWith("uia.", invocation.Method, StringComparison.Ordinal);

        // The handler runs on the app's own thread, not ours: this only fires if the pattern really reached
        // the control.
        Assert.True(host.Pressed.Wait(TimeSpan.FromSeconds(10)), "the button's Click handler never ran");

        // And nothing was clicked: the pointer is exactly where it was.
        GetCursorPos(out var cursorAfter);
        Assert.Equal((cursorBefore.X, cursorBefore.Y), (cursorAfter.X, cursorAfter.Y));
    }

    [LiveDesktopFact]
    public async Task Acting_on_a_named_control_reuses_the_observation_that_already_walked_the_window()
    {
        // The observation walked this window to re-check the action; the act then walked it again to turn the
        // id back into a control. That second walk was a cross-process RPC per node for an answer the caller
        // was already holding — and the reason an element-addressed action cost twice what it should.
        //
        // What is asserted here is the whole path, because the saving is only real if the reuse still presses
        // the control the id named: a cache that returned the wrong control would be cheaper and wrong.
        using var host = new ButtonWindow();
        using var automation = new DesktopAutomation();

        automation.Observe();

        var button = automation.LastObservation!.Elements.FirstOrDefault(element =>
            element.WindowId == host.HandleHex && element.Name == "提交");

        Assert.NotNull(button);

        GetCursorPos(out var cursorBefore);

        var action = await automation.ActAsync(
            host.HandleHex,
            ActionKind.LeftClick,
            new ActionParams { ElementId = button!.ElementId });

        Assert.True(action.GuardPassed, action.GuardReason);
        Assert.NotNull(action.Injection);
        Assert.True(action.Injection!.Ok, action.Injection.Detail);

        // The pattern reached the control, so no coordinate click was injected and the pointer did not move.
        Assert.StartsWith("uia.", action.Injection.Detail, StringComparison.Ordinal);
        Assert.True(host.Pressed.Wait(TimeSpan.FromSeconds(10)), "the button's Click handler never ran");

        GetCursorPos(out var cursorAfter);
        Assert.Equal((cursorBefore.X, cursorBefore.Y), (cursorAfter.X, cursorAfter.Y));
    }

    [LiveDesktopFact]
    public async Task An_injected_click_reaches_a_real_button()
    {
        // The missing half of the proof: the pattern path was verified against a real control, but nothing
        // had ever verified that a *click* — SendInput, the path every coordinate action takes — actually
        // presses anything. It is the path a Chromium window (网易云音乐, Electron apps) needs, because UIA
        // exposes nothing usable there.
        using var host = new ButtonWindow();
        using var automation = new DesktopAutomation();

        var observation = automation.Observe();
        Assert.NotNull(observation.Find(host.HandleHex));

        // The host is TopMost, so this is about the click, not about z-order: whatever else is open cannot
        // cover the button. Focusing first is what a person does before clicking.
        var focused = new InputInjector().Focus(host.Handle);
        Assert.True(focused.Ok, focused.Detail);

        GetCursorPos(out var start);

        var mediaDir = Directory.CreateTempSubdirectory("partner-live-act-media-");
        try
        {
            var tool = new DesktopActTool(
                automation,
                DesktopToolPolicy.Any,
                new FileMediaStore(mediaDir.FullName));
            var result = await tool.InvokeAsync(
                $$"""{"entityId":"{{host.HandleHex}}","kind":"LeftClick","x":{{host.ButtonCentre.X}},"y":{{host.ButtonCentre.Y}}}""");

            Assert.True(result.Ok, result.Content);
            Assert.Contains("注入成功", result.Content, StringComparison.Ordinal);

            // The handler runs on the application's own thread: this only fires if the injected click really
            // reached the control.
            Assert.True(host.Pressed.Wait(TimeSpan.FromSeconds(10)), $"the button's Click handler never ran: {result.Content}");

            // And the answer now carries the state after the click — the same pipeline the screenshot tool
            // runs, attached to the result the model is already about to read. One completed answer replaces
            // the follow-up call the loop used to need.
            var afterPicture = Assert.Single(result.Images!);
            Assert.StartsWith("data:image/png;base64,", afterPicture);
            Assert.Contains("已附截图", result.Content, StringComparison.Ordinal);
            Assert.Contains("px/py 可直接对它用", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            SetCursorPos(start.X, start.Y);
            mediaDir.Delete(recursive: true);
        }
    }

    [LiveDesktopFact]
    public async Task A_click_covered_by_another_window_is_brought_to_the_front_and_lands()
    {
        // The measured failure this reproduces: the agent's own window sits over the target application, the
        // click is delivered to the cover, and the tool still reported "注入成功 · 画面无变化" while the target
        // never received anything. Raising the target is what the refusal used to ask the model to do; it now
        // happens inside the same action, so one approval and one round trip replace three.
        using var target = new ButtonWindow("partner-live-target", new Point(120, 120));
        using var cover = new ButtonWindow("partner-live-cover", new Point(60, 60));

        // Both are put into the topmost band explicitly: `Form.TopMost` was measured not to take effect on
        // these forms, and a normal window can never be raised above a topmost one — which would make the
        // raise impossible rather than hard. In the product the cover is the agent's own window.
        target.Raise();
        cover.Raise();

        using var automation = new DesktopAutomation();
        var observation = automation.Observe();
        Assert.NotNull(observation.Find(target.HandleHex));

        var mediaDirCovered = Directory.CreateTempSubdirectory("partner-live-cover-media-");
        try
        {
            var tool = new DesktopActTool(
                automation, DesktopToolPolicy.Any, new FileMediaStore(mediaDirCovered.FullName));
            var click = $$"""{"entityId":"{{target.HandleHex}}","kind":"LeftClick","x":{{target.ButtonCentre.X}},"y":{{target.ButtonCentre.Y}}}""";

            // The setup itself: the point really is covered, by the cover window, before anything is clicked.
            cover.Raise();
            var under = HitTest.At(target.ButtonCentre.X, target.ButtonCentre.Y);
            Assert.True(
                under.Handle == cover.Handle,
                $"expected the cover under ({target.ButtonCentre.X},{target.ButtonCentre.Y}), got {under.Handle:x} '{under.Title}'");

            var clicked = await tool.InvokeAsync(click);

            Assert.True(clicked.Ok, clicked.Content);
            Assert.Contains("带到前台", clicked.Content, StringComparison.Ordinal);
            Assert.True(target.Pressed.Wait(TimeSpan.FromSeconds(10)), $"the button's Click handler never ran: {clicked.Content}");
        }
        finally
        {
            mediaDirCovered.Delete(recursive: true);
        }
    }

    [LiveDesktopFact]
    public async Task A_click_covered_by_a_window_that_cannot_be_beaten_is_refused_by_name()
    {
        // The cover is topmost and the target is not: raising the target cannot put it above the cover, and
        // pretending otherwise would inject a click into the cover. The refusal names what is in the way —
        // and nothing was pressed anywhere.
        using var target = new ButtonWindow("partner-live-target", new Point(120, 120), topMost: false);
        using var cover = new ButtonWindow("partner-live-cover", new Point(60, 60), topMost: false);

        cover.Raise();

        using var automation = new DesktopAutomation();
        var observation = automation.Observe();
        Assert.NotNull(observation.Find(target.HandleHex));

        var mediaDirRefused = Directory.CreateTempSubdirectory("partner-live-refused-media-");
        try
        {
            var tool = new DesktopActTool(
                automation, DesktopToolPolicy.Any, new FileMediaStore(mediaDirRefused.FullName));
            var click = $$"""{"entityId":"{{target.HandleHex}}","kind":"LeftClick","x":{{target.ButtonCentre.X}},"y":{{target.ButtonCentre.Y}}}""";

            var refused = await tool.InvokeAsync(click);

            Assert.False(refused.Ok, refused.Content);
            Assert.Contains("partner-live-cover", refused.Content, StringComparison.Ordinal);
            Assert.Contains("仍然被挡住", refused.Content, StringComparison.Ordinal);

            // Nothing was injected, so nothing is new to show: a refusal does not carry a picture.
            Assert.Null(refused.Images);

            // And nothing was pressed anywhere.
            Assert.False(target.Pressed.IsSet);
            Assert.False(cover.Pressed.IsSet);
        }
        finally
        {
            mediaDirRefused.Delete(recursive: true);
        }
    }

    [LiveDesktopFact]
    public async Task The_picture_of_a_window_is_aligned_with_the_window_rect()
    {
        // Everything the model reads off a picture — a grid label, a mark — is expressed in the window's
        // screen rectangle, and `desktop_act` takes screen coordinates. If the pixels are shifted relative to
        // that rectangle, every coordinate derived from the picture is off by the same amount, and the click
        // lands beside the control with no error anywhere in the pipeline.
        using var host = new ButtonWindow();
        using var automation = new DesktopAutomation();

        var observation = automation.Observe();
        var window = observation.Find(host.HandleHex);
        Assert.NotNull(window);

        var frame = await automation.Capture.CaptureWindowAsync(host.Handle);
        Assert.True(frame.Ok, frame.Error);

        var expectedX = host.MarkerRect.X - window!.Rect.X;
        var expectedY = host.MarkerRect.Y - window.Rect.Y;

        var found = FindColour(frame.Bgra, frame.Width, frame.Height, ButtonWindow.MarkerColour);

        Assert.True(found.X >= 0, $"the marker colour is not in the picture at all (method={frame.Method})");
        Assert.True(
            Math.Abs(found.X - expectedX) <= 2 && Math.Abs(found.Y - expectedY) <= 2,
            $"the picture is shifted: marker at picture ({found.X},{found.Y}), window rect says ({expectedX},{expectedY}) "
                + $"— offset ({found.X - expectedX},{found.Y - expectedY}); method={frame.Method} "
                + $"window={window.Rect.Width}x{window.Rect.Height} frame={frame.Width}x{frame.Height}");
    }

    /// <summary>Topmost-leftmost pixel of an exact BGRA colour, or (-1,-1) when it is not there.</summary>
    private static (int X, int Y) FindColour(byte[] bgra, int width, int height, System.Drawing.Color colour)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                if (bgra[i] == colour.B && bgra[i + 1] == colour.G && bgra[i + 2] == colour.R)
                {
                    return (x, y);
                }
            }
        }

        return (-1, -1);
    }

    [LiveDesktopFact]
    public void An_action_a_pattern_cannot_express_falls_back_to_injected_input()
    {
        using var host = new ButtonWindow();
        using var automation = new DesktopAutomation();

        var observation = automation.Observe();
        var button = observation.Elements.FirstOrDefault(element =>
            element.WindowId == host.HandleHex && element.Name == "提交");

        Assert.NotNull(button);

        // A wheel is a pointer-and-focus gesture; there is no pattern for it. Null is what tells the caller to
        // inject instead — if this returned something, scrolling would silently do nothing.
        Assert.Null(UiaTree.TryAct(host.Handle, host.HandleHex, button!.ElementId, ActionKind.Scroll));
        Assert.Null(UiaTree.TryAct(host.Handle, host.HandleHex, button.ElementId, ActionKind.TypeText));

        // A stale id must not resolve to some other control: the tree is bounded, so an index past the end is
        // simply not found.
        Assert.Null(UiaTree.TryAct(host.Handle, host.HandleHex, $"{host.HandleHex}:9999", ActionKind.LeftClick));
        Assert.Null(UiaTree.TryAct(host.Handle, host.HandleHex, "not-an-element-id", ActionKind.LeftClick));
    }

    [LiveDesktopFact]
    public async Task A_picture_of_a_real_window_marks_its_control_where_the_application_says_it_is()
    {
        // The measured failure this exists for: asked for a play button at screen (1280,1032), a real session
        // produced (957,948) — 334 px away, in empty background. Nothing to measure against. This proves that
        // a control the observation found is marked, and that the coordinate handed to the model is the
        // control's real position, not an estimate.
        using var host = new ButtonWindow();
        using var automation = new DesktopAutomation();

        var observation = automation.Observe();

        var window = observation.Find(host.HandleHex);
        Assert.NotNull(window);

        var elements = observation.Elements
            .Where(element => string.Equals(element.WindowId, host.HandleHex, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(elements);

        var frame = await automation.Capture.CaptureWindowAsync(host.Handle);
        Assert.True(frame.Ok, frame.Error);

        var source = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Width * 4);

        var (annotated, marks) = ScreenshotMarks.Draw(source, window!.Rect, elements);

        var button = marks.FirstOrDefault(mark => mark.Label == "提交");
        Assert.NotNull(button);

        // The oracle is the control's own PointToScreen — the same answer the application would give.
        Assert.InRange(button!.ClickAt.X, host.ButtonCentre.X - 2, host.ButtonCentre.X + 2);
        Assert.InRange(button.ClickAt.Y, host.ButtonCentre.Y - 2, host.ButtonCentre.Y + 2);

        // And the picture really carries the mark: the chip sits on the control's top-left corner, which is the
        // pixel that was grey-ish before and is the mark colour now. (The window's own origin is not a multiple
        // of the grid step, so "there is a line at x=0" would be an assumption about the step, not about the
        // mark — the unit tests pin the grid geometry against a controlled origin.)
        var pictureX = button.Rect.X - window.Rect.X;
        var pictureY = button.Rect.Y - window.Rect.Y;

        Assert.Equal(frame.Width, annotated.PixelWidth);
        Assert.NotEqual(Pixel(source, pictureX, pictureY), Pixel(annotated, pictureX, pictureY));
    }

    private static int Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[0] | pixel[1] << 8 | pixel[2] << 16;
    }

    /// <summary>
    /// A real window with a real button, running its own message loop on an STA thread for the life of the
    /// test. UIA can only be proven against a live control — a stub would prove nothing about the walk, the
    /// pattern lookup or the cross-thread invoke.
    /// </summary>
    private sealed class ButtonWindow : IDisposable
    {
        private readonly string _title;
        private readonly Point _location;
        private readonly bool _topMost;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _closed = new(false);
        private readonly Thread _thread;
        private Form? _form;

        public ButtonWindow(string title = "partner-uia-live-test", Point? location = null, bool topMost = true)
        {
            _title = title;
            _location = location ?? new Point(60, 60);
            _topMost = topMost;

            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            Assert.True(_ready.Wait(TimeSpan.FromSeconds(15)), "the control host never came up");
        }

        public nint Handle { get; private set; }

        public string HandleHex => Handle.ToString("x");

        /// <summary>
        /// The button's centre in screen coordinates, straight from the control itself. An independent oracle:
        /// the test compares what the server computed against what the application says, so the assertion does
        /// not just restate the pipeline's own arithmetic.
        /// </summary>
        public (int X, int Y) ButtonCentre { get; private set; }

        public ManualResetEventSlim Pressed { get; } = new(false);

        /// <summary>A colour nothing else in the window uses; the alignment test looks for it in a capture.</summary>
        public static System.Drawing.Color MarkerColour { get; } = System.Drawing.Color.FromArgb(0x11, 0x77, 0xCC);

        /// <summary>The marker panel's rectangle in screen coordinates, straight from the control.</summary>
        public Rectangle MarkerRect { get; private set; }

        /// <summary>
        /// Puts this window at the top of the topmost band. Being created last is not enough: the window the
        /// user is typing into can be topmost too (a harness window, a widget), and then it sits above a
        /// window that merely asked to be topmost earlier.
        /// </summary>
        public void Raise() =>
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        public void Dispose()
        {
            try
            {
                _form?.BeginInvoke(new Action(() => _form.Close()));
            }
            catch (InvalidOperationException)
            {
                // Already closing.
            }

            _closed.Wait(TimeSpan.FromSeconds(5));
            Pressed.Dispose();
            _ready.Dispose();
        }

        private void Run()
        {
            _form = new Form
            {
                Text = _title,
                ClientSize = new Size(360, 200),
                StartPosition = FormStartPosition.Manual,
                Location = _location,
                ShowInTaskbar = false,
                TopMost = _topMost,
            };

            var button = new Button
            {
                Text = "提交",
                Name = "submit",
                Location = new Point(40, 40),
                Size = new Size(160, 44),
            };

            button.Click += (_, _) => Pressed.Set();
            _form.Controls.Add(button);

            // A colour nothing else in the window uses, at a known client position: the oracle for "is the
            // picture aligned with the window rectangle the coordinates are expressed in".
            var marker = new Panel
            {
                BackColor = MarkerColour,
                Location = new Point(260, 40),
                Size = new Size(60, 40),
            };

            _form.Controls.Add(marker);

            _form.Shown += (_, _) =>
            {
                Handle = _form.Handle;

                var centre = button.PointToScreen(new System.Drawing.Point(button.Width / 2, button.Height / 2));
                var centreX = (double)centre.X;
                var centreY = (double)centre.Y;
                ButtonCentre = ((int)Math.Round(centreX), (int)Math.Round(centreY));

                var markerOrigin = marker.PointToScreen(new System.Drawing.Point(0, 0));
                MarkerRect = new Rectangle(markerOrigin, marker.Size);

                _ready.Set();
            };

            _form.FormClosed += (_, _) => _closed.Set();

            Application.Run(_form);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly nint HwndTopmost = new(-1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LiveDesktopFact]
    public async Task A_real_window_respects_the_contract_on_the_wire()
    {
        // End to end, and safe: a key press at a background window is refused before any input is
        // injected — and the refusal names the CONTROL it resolved to, which is the evidence that the
        // snap went through rather than the window-level fallback.
        var workspace = Directory.CreateTempSubdirectory("partner-snap-live-");
        try
        {
            using var automation = new DesktopAutomation();
            var observation = automation.Observe();
            var target = observation.Entities.FirstOrDefault(entity =>
                !entity.IsForeground && !entity.IsMinimized && entity.Rect.Width > 100);
            var element = observation.Elements.FirstOrDefault(e => e.WindowId == target!.EntityId);
            if (target is null || element is null)
            {
                // No background window with an addressable control in this session: the pure-geometry
                // tests above carry the contract.
                return;
            }

            var tool = new DesktopScreenshotTool(automation, new FileMediaStore(workspace.FullName));
            var shot = await tool.InvokeAsync(
                $$"""{"entityId":"{{target.EntityId}}","maxDimension":4096}""");
            Assert.True(shot.Ok, shot.Content);

            var mediaId = System.Text.RegularExpressions.Regex.Match(
                shot.Content, @"媒体 ([0-9a-f]{8,})").Groups[1].Value;
            Assert.NotEmpty(mediaId);
            var picture = automation.PictureOf(mediaId);
            Assert.NotNull(picture);

            // A miss on purpose: five pixels right of the centre, still within the snap radius.
            var px = (int)Math.Round((element.Centre.X + 5 - picture!.ScreenRect.X) * picture.PictureWidth / (double)picture.ScreenRect.Width);
            var py = (int)Math.Round((element.Centre.Y - picture.ScreenRect.Y) * picture.PictureHeight / (double)picture.ScreenRect.Height);

            var media = new FileMediaStore(workspace.FullName);
            var actTool = new DesktopActTool(automation, DesktopToolPolicy.Any, media);
            var result = await actTool.InvokeAsync(
                $$"""{"kind":"KeyPress","virtualKey":67,"px":{{px}},"py":{{py}},"mediaId":"{{mediaId}}","feedback":"off"}""");

            Assert.False(result.Ok, $"audit={result.Content}");
            Assert.Contains(element.Name, result.Content, StringComparison.Ordinal);

            // The snap is announced, so a model reading 5 px of drift knows the server already smoothed it.
            Assert.Contains("按控件中心点执行", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
