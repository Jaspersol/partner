using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// A control that reports its own state is verified by that state, not by a pixel diff over a window.
/// <para>
/// Live: it shows a real window with a real check box, finds it through UI Automation and clicks it. Skipped
/// unless <c>PARTNER_DESKTOP_LIVE=1</c>, because it puts a window on the user's screen.
/// </para>
/// </summary>
public sealed class StateVerificationLiveTests
{
    private sealed class LiveDesktopFactAttribute : FactAttribute
    {
        public LiveDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run (it shows a window of its own)";
            }
        }
    }

    private sealed class CheckBoxWindow : IDisposable
    {
        private readonly System.Windows.Forms.Form _form;

        public CheckBoxWindow()
        {
            _form = new System.Windows.Forms.Form { Text = "partner-state-probe", Width = 500, Height = 400 };
            Box = new System.Windows.Forms.CheckBox { Text = "记住我", Left = 40, Top = 40, Width = 200 };
            _form.Controls.Add(Box);
            _form.Show();

            for (var attempt = 0; attempt < 100 && _form.Handle == nint.Zero; attempt++)
            {
                Thread.Sleep(20);
            }

            HandleHex = _form.Handle.ToString("x");
        }

        public System.Windows.Forms.CheckBox Box { get; }

        public string HandleHex { get; }

        public void Dispose() => _form.Dispose();
    }

    [LiveDesktopFact]
    public async Task Clicking_a_check_box_is_verified_by_its_state_with_no_picture_compared()
    {
        using var window = new CheckBoxWindow();
        using var automation = new DesktopAutomation();

        var observation = automation.Observe(elementsFor: window.HandleHex);
        var box = observation.Elements.FirstOrDefault(e => e.WindowId == window.HandleHex && e.Name == "记住我");
        Assert.NotNull(box);
        Assert.False(window.Box.Checked);

        var action = await automation.ActAsync(
            window.HandleHex,
            ActionKind.LeftClick,
            new ActionParams { ElementId = box!.ElementId });

        Assert.True(action.GuardPassed, action.GuardReason);
        Assert.True(window.Box.Checked, "the check box should have been toggled");
        Assert.NotNull(action.PostVerify);
        Assert.Equal(VerifyDecision.AnyChange, action.PostVerify!.Decision);
        Assert.Equal("uia.state", action.PostVerify.Source);
        Assert.Contains("开关", action.PostVerify.Reason!, StringComparison.Ordinal);
    }
}
