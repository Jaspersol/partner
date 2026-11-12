using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;

/// <summary>
/// Live proof of the two rules a stub cannot answer: a password field's value never leaves the provider, and
/// a keyboard action lands in the control it names rather than in whichever field happened to have the caret.
/// </summary>
public sealed class DesktopLiveSafetyTests
{
    private sealed class LiveDesktopFactAttribute : FactAttribute
    {
        public LiveDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run";
            }
        }
    }

    [LiveDesktopFact]
    public void A_password_field_is_secret_and_its_value_never_reaches_the_walk()
    {
        using var host = new SafetyWindow();

        var (elements, nodes, stopped) = UiaTree.ReadNumberedWithNodes(host.Handle, host.HandleHex);
        var secretIndex = elements.ToList().FindIndex(element => element.Secret);
        Assert.True(secretIndex >= 0, $"the password field was not reported as secret (walk={stopped}): "
            + string.Join("；", elements.Select(element => $"{element.Name}/{element.Role}/secret={element.Secret}")));

        var secret = elements[secretIndex];
        Assert.Null(secret.Value);

        var typed = UiaTree.TryAct(
            host.Handle,
            host.HandleHex,
            secret.ElementId,
            ActionKind.TypeText,
            (elements, nodes),
            new ActionParams { Text = "hunter2" },
            background: true);

        Assert.NotNull(typed);
        Assert.True(typed!.Ok, typed.Detail);

        var state = nodes[secretIndex].ReadState();
        Assert.NotNull(state);
        Assert.Contains("已填", state, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", state, StringComparison.Ordinal);
    }

    [LiveDesktopFact]
    public void Keyboard_input_goes_to_the_named_control_not_the_one_that_had_focus()
    {
        using var host = new SafetyWindow();

        var (elements, nodes, _) = UiaTree.ReadNumberedWithNodes(host.Handle, host.HandleHex);
        var first = elements.First(element => element.Name.Contains("first", StringComparison.OrdinalIgnoreCase));
        var second = elements.First(element => element.Name.Contains("second", StringComparison.OrdinalIgnoreCase));

        var focused = UiaTree.TryAct(
            host.Handle, host.HandleHex, first.ElementId, ActionKind.Focus, (elements, nodes));
        Assert.NotNull(focused);
        Assert.True(focused!.Ok, focused.Detail);

        // The caret is in `first`; typing was aimed at `second`. The gate must move the focus, not write
        // into whichever field happens to have it.
        var refusal = UiaTree.EnsureKeyboardFocus(host.Handle, host.HandleHex, second.ElementId, (elements, nodes));
        Assert.Null(refusal);

        var secondIndex = elements.ToList().FindIndex(element => element.ElementId == second.ElementId);
        var firstIndex = elements.ToList().FindIndex(element => element.ElementId == first.ElementId);
        Assert.True(nodes[secondIndex].HasKeyboardFocus(), "the named control did not take the keyboard focus");
        Assert.False(nodes[firstIndex].HasKeyboardFocus(), "the old control still has the keyboard focus");
    }

    [LiveDesktopFact]
    public void A_capture_whose_window_moves_is_discarded_instead_of_judged()
    {
        using var host = new SafetyWindow();
        var capture = new MovingCapture(host);
        var verifier = new PostActionVerifier(capture);

        var identity = WindowEnumerator.IdentityOf(host.Handle);
        Assert.NotNull(identity);
        var before = new CaptureResult
        {
            Ok = true,
            Width = identity.Value.Width,
            Height = identity.Value.Height,
            Bgra = new byte[identity.Value.Width * identity.Value.Height * 4],
            Method = "test.before",
            ScreenRect = new PixelRect(identity.Value.X, identity.Value.Y, identity.Value.Width, identity.Value.Height),
        };

        var result = verifier.VerifyAsync(host.Handle, new PixelRect(0, 0, before.Width, before.Height), before)
            .GetAwaiter()
            .GetResult();

        Assert.Equal(VerifyDecision.Indeterminate, result.Decision);
        Assert.Contains("窗口在验证期间", result.Reason!, StringComparison.Ordinal);
    }

    [LiveDesktopFact]
    public void A_hung_window_is_reported_as_unresponsive_without_waiting_for_uia()
    {
        using var host = new HungWindow();

        // Give the Shown handler its next instruction (the sleep) before probing, or the probe can win the
        // race and get a live answer from a window that is about to hang.
        Thread.Sleep(200);
        var watch = Stopwatch.StartNew();

        var (_, _, stopped) = UiaTree.ReadNumberedWithNodes(host.Handle, host.HandleHex);

        watch.Stop();
        Assert.Equal(WalkStopReason.Unresponsive, stopped);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3.5), $"the probe took {watch.Elapsed}");
    }

    /// <summary>A real window with two text fields and a password field, on its own STA message loop.</summary>
    private sealed class SafetyWindow : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _closed = new(false);
        private readonly Thread _thread;
        private Form? _form;

        public SafetyWindow()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(15)), "the safety window never came up");
        }

        public nint Handle { get; private set; }

        public string HandleHex => Handle.ToString("x");

        public void MoveBy(int dx, int dy) => _form?.BeginInvoke(new Action(() =>
        {
            _form.Location = new Point(_form.Location.X + dx, _form.Location.Y + dy);
        }));

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
            _ready.Dispose();
        }

        private void Run()
        {
            _form = new Form
            {
                Text = "partner-uia-secret-test",
                ClientSize = new Size(360, 220),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(80, 80),
                ShowInTaskbar = false,
                TopMost = true,
            };

            _form.Controls.Add(new TextBox
            {
                Name = "first",
                AccessibleName = "first",
                Location = new Point(20, 20),
                Size = new Size(300, 28),
            });

            _form.Controls.Add(new TextBox
            {
                Name = "second",
                AccessibleName = "second",
                Location = new Point(20, 60),
                Size = new Size(300, 28),
            });

            _form.Controls.Add(new TextBox
            {
                Name = "secret",
                AccessibleName = "密码",
                Location = new Point(20, 100),
                Size = new Size(300, 28),
                UseSystemPasswordChar = true,
            });

            _form.Shown += (_, _) =>
            {
                Handle = _form.Handle;
                _ready.Set();
            };

            _form.FormClosed += (_, _) => _closed.Set();
            Application.Run(_form);
        }
    }

    /// <summary>A capture that moves the window before returning its frame: the before/after identities differ.</summary>
    private sealed class MovingCapture(SafetyWindow host) : IScreenCapture
    {
        private int _calls;

        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                host.MoveBy(40, 30);
                Thread.Sleep(150);
            }

            return Task.FromResult(new CaptureResult
            {
                Ok = true,
                Width = 360,
                Height = 220,
                Bgra = new byte[360 * 220 * 4],
                Method = "test.moving",
                ScreenRect = new PixelRect(80, 80, 360, 220),
            });
        }

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }

    /// <summary>A window whose UI thread is blocked after it is shown, so its message loop cannot answer.</summary>
    private sealed class HungWindow : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _closed = new(false);
        private readonly Thread _thread;
        private Form? _form;

        public HungWindow()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(15)), "the hung window never came up");
        }

        public nint Handle { get; private set; }

        public string HandleHex => Handle.ToString("x");

        public void Dispose()
        {
            try
            {
                _form?.BeginInvoke(new Action(() => _form.Close()));
            }
            catch (InvalidOperationException)
            {
            }

            _closed.Wait(TimeSpan.FromSeconds(10));
            _ready.Dispose();
        }

        private void Run()
        {
            _form = new Form
            {
                Text = "partner-uia-hung-test",
                ClientSize = new Size(280, 140),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(120, 120),
                ShowInTaskbar = false,
            };

            _form.Shown += (_, _) =>
            {
                Handle = _form.Handle;
                _ready.Set();

                // The message loop is still running when Shown returns; block it, which is what a hung
                // application looks like to every UI Automation client.
                Thread.Sleep(4000);
            };

            _form.FormClosed += (_, _) => _closed.Set();
            Application.Run(_form);
        }
    }
}
