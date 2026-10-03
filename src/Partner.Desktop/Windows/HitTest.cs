using Partner.Desktop.Interop;

namespace Partner.Desktop.Windows;

/// <summary>What a screen point delivers a click to.</summary>
/// <param name="Handle">The top-level window under the point, or 0 when nothing is there.</param>
/// <param name="ProcessId">Owning process, for the refusal message.</param>
/// <param name="Title">What a person would call it, for the refusal message.</param>
/// <param name="RootOwner">The window that owns this one, resolved through the parent and owner chains. A
/// menu or popup answers the window it belongs to, which is what tells an application's own popup apart
/// from a window that merely happens to be in the way.</param>
public sealed record PointTarget(nint Handle, int ProcessId, string Title, nint RootOwner);

/// <summary>
/// The window a screen point actually belongs to.
/// <para>
/// Pointer input is global: the OS delivers it to whatever top-level window is under the point, not to the
/// window a caller named. So this — not the rectangle an observation reported — is what decides where a
/// click lands, and it is the only way to tell a click that will reach the target from one that will be
/// swallowed by whatever is covering it.
/// </para>
/// </summary>
public static class HitTest
{
    /// <summary>
    /// The top-level window at this point. A control resolves to the window that owns it, because that is
    /// the window a click on the control is delivered through.
    /// </summary>
    public static PointTarget At(int x, int y)
    {
        var window = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y });
        if (window == 0)
        {
            return new PointTarget(0, 0, string.Empty, 0);
        }

        var root = NativeMethods.GetAncestor(window, NativeMethods.GaRoot);
        if (root == 0)
        {
            root = window;
        }

        var owner = NativeMethods.GetAncestor(window, NativeMethods.GaRootOwner);
        NativeMethods.GetWindowThreadProcessId(root, out var processId);

        return new PointTarget(root, (int)processId, WindowEnumerator.GetTitle(root), owner);
    }
}
