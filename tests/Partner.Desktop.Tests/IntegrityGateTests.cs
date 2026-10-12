using Partner.Desktop;
using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;

/// <summary>
/// The UIPI gate: an input sent toward a higher-integrity window is not refused by Windows, it is silently
/// dropped — <c>SendInput</c> reports success and the elevated application never sees the event. These tests
/// hold the two sides of that: the swallowed input becomes a named refusal before anything happens (and
/// before the user's window order is touched for a doomed action), and the paths that do not travel through
/// <c>SendInput</c> stay open.
/// </summary>
public sealed class IntegrityGateTests
{
    [Fact]
    public async Task A_click_toward_an_elevated_window_is_refused_before_anything_happens()
    {
        GetAutomation(out var automation, out var observe);

        var action = await Act(automation, observe.EntityId, observe.Rect, ActionKind.LeftClick);

        Assert.False(action.GuardPassed);
        Assert.Null(action.Injection);
        Assert.Null(action.PostVerify);
        Assert.Equal("ELEVATED", action.GuardReason);
    }

    [Fact]
    public async Task A_keyboard_action_toward_an_elevated_window_is_refused_the_same_way()
    {
        // Typing and keys travel through SendInput too, and the guard that requires a foreground window
        // cannot save them: the foreground process being elevated is precisely the silent case.
        GetAutomation(out var automation, out var observe);

        var action = await automation.ActAsync(
            observe.EntityId,
            ActionKind.KeyPress,
            new ActionParams { VirtualKey = 0x20 });

        Assert.False(action.GuardPassed);
        Assert.Null(action.Injection);
        Assert.Equal("ELEVATED", action.GuardReason);
    }

    [Fact]
    public void Focus_and_scrollIntoView_do_not_travel_through_SendInput_and_so_are_not_gated()
    {
        // Focus is a window-manager call, and activating an elevated window is what a person's alt-tab does.
        // ScrollIntoView is a UI Automation pattern call. Both cross integrity levels; everything else here
        // ends in an input event.
        Assert.False(DesktopAutomation.DeliversInput(ActionKind.Focus));
        Assert.False(DesktopAutomation.DeliversInput(ActionKind.ScrollIntoView));

        var inputKinds = new[]
        {
            ActionKind.MovePointer, ActionKind.LeftClick, ActionKind.RightClick,
            ActionKind.KeyPress, ActionKind.TypeText, ActionKind.Scroll, ActionKind.Drag,
        };

        Assert.All(inputKinds, kind => Assert.True(
            DesktopAutomation.DeliversInput(kind),
            $"{kind} sends input and has to be gated"));
    }

    [Fact]
    public void The_refusal_names_the_cause_and_the_actually_actionable_way_out()
    {
        // "It failed" sends the model into a retry the OS will drop every time. The message has to say what
        // Windows did — swallowed, not refused — and what ends it: matching permissions.
        var reason = IntegrityGate.ElevatedRefusal(1234);

        Assert.Contains("管理员", reason, StringComparison.Ordinal);
        Assert.Contains("静默丢弃", reason, StringComparison.Ordinal);
        Assert.Contains("1234", reason, StringComparison.Ordinal);
        Assert.Contains("重启本应用", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undetermined_or_lower_level_is_never_a_refusal()
    {
        // Fail-open: the confirmed cases are the no-op cases; a level that cannot be read stays a guess, and
        // refusing on a guess is how a working flow becomes a refusals-only flow.
        Assert.Null(new IntegrityGate().Refusal(0));

        var shell = NativeMethods.GetShellWindow();
        if (shell != 0)
        {
            // The desktop (Progman) runs at medium integrity; this process matches or exceeds it whatever it
            // is run at, so there is nothing to refuse here. If Windows has no shell window, the skip is fine.
            Assert.Null(new IntegrityGate().Refusal(shell));
        }
    }

    /// <summary>
    /// A real observation of the desktop (nothing in it is touched), with the gate answered by a stub. Real
    /// windows are used because the gate sits behind the lease validation, and the lease validation needs
    /// observations — the part around the gate is the part under test here; the message is unit-tested above.
    /// </summary>
    private static void GetAutomation(
        out DesktopAutomation automation,
        out (string EntityId, PixelRect Rect) observe)
    {
        automation = new DesktopAutomation(integrityRefusal: handle => "ELEVATED");
        var observation = automation.Observe();
        var entity = observation.Entities.First(e => !e.IsMinimized);
        observe = (entity.EntityId, entity.Rect);
    }

    private static async Task<DesktopAction> Act(
        DesktopAutomation automation,
        string entityId,
        PixelRect rect,
        ActionKind kind) => await automation.ActAsync(
        entityId,
        kind,
        new ActionParams
        {
            // The centre of the observed rectangle: inside it, and a point the guard's containment
            // check accepts, so the only refusal that can come back is the gate's.
            X = rect.X + rect.Width / 2,
            Y = rect.Y + rect.Height / 2,
        });
}
