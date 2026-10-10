using System.Diagnostics;
using Partner.Desktop;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// What a full observation costs on a window shaped like the ones this budget exists for.
/// <para>
/// The window here is a real one with two thousand controls the walk has no use for — the shape a Chromium or
/// Electron window presents, where almost every node is an element the walk skips. Under the old bound, which
/// counted only elements it <i>produced</i>, that window was walked to its last node: measured at 23.8s for
/// this one window, which on a nine-window desktop is the whole observation gone.
/// </para>
/// <para>
/// The bound here is deliberately loose — the point is not to pin a number that moves with the machine — and
/// the two buttons are added first, so the test also says the cap does not cost us controls: the same single
/// button is found before and after.
/// </para>
/// </summary>
public sealed class ObservationCostTests
{
    private sealed class LiveDesktopFactAttribute : FactAttribute
    {
        public LiveDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run "
                    + "(it shows a window of its own)";
            }
        }
    }

    /// <summary>More than the visit cap, so an unbounded walk would have to cross it.</summary>
    private const int SkippedControls = 2000;

    /// <summary>
    /// Comfortably under the time budget and far above what the tree costs, so this fails only if the walk
    /// became unbounded again — never because the machine is busy.
    /// </summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    [LiveDesktopFact]
    public void A_window_full_of_controls_nobody_asked_for_is_walked_within_its_budget()
    {
        using var host = new WideWindow(SkippedControls);
        using var automation = new DesktopAutomation();

        var watch = Stopwatch.StartNew();
        var observation = automation.Observe();
        watch.Stop();

        var button = observation.Elements.FirstOrDefault(element =>
            element.WindowId == host.HandleHex && element.Name == "提交");

        Assert.NotNull(button);
        Assert.True(
            watch.Elapsed < Ceiling,
            $"observing one window took {watch.Elapsed.TotalMilliseconds:0}ms, which is the unbounded walk again");
    }

    [LiveDesktopFact]
    public void A_real_window_is_observed_quickly()
    {
        // The other side of the same bound: the caps exist for pathological trees, and a window that is merely
        // ordinary must not feel them. This is the number a turn actually pays on a quiet desktop.
        using var host = new WideWindow(24);
        using var automation = new DesktopAutomation();

        var watch = Stopwatch.StartNew();
        var observation = automation.Observe();
        watch.Stop();

        Assert.NotNull(observation.Elements.FirstOrDefault(element =>
            element.WindowId == host.HandleHex && element.Name == "提交"));
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(2),
            $"observing an ordinary window took {watch.Elapsed.TotalMilliseconds:0}ms");
    }

    /// <summary>A shown window with one named button and <paramref name="skipped"/> controls it does not need.</summary>
    private sealed class WideWindow : IDisposable
    {
        private readonly System.Windows.Forms.Form _form;

        public WideWindow(int skipped)
        {
            _form = new System.Windows.Forms.Form
            {
                Text = "partner-observation-cost-probe",
                Width = 900,
                Height = 700,
                ShowInTaskbar = true,
            };

            // The control worth finding comes first, which is the common shape and the one that makes the
            // comparison fair: the old code found it too, twenty seconds later.
            _form.Controls.Add(new System.Windows.Forms.Button { Text = "提交", Left = 700, Top = 600 });

            for (var index = 0; index < skipped; index++)
            {
                _form.Controls.Add(new System.Windows.Forms.PictureBox
                {
                    Left = 10,
                    Top = 10 + ((index % 300) * 2),
                    Width = 60,
                    Height = 12,
                });
            }

            _form.Show();

            // A handle exists only once the window is shown, and the walk needs one.
            for (var attempt = 0; attempt < 100 && _form.Handle == nint.Zero; attempt++)
            {
                Thread.Sleep(20);
            }

            HandleHex = _form.Handle.ToString("x");
        }

        public string HandleHex { get; }

        public void Dispose() => _form.Dispose();
    }
}