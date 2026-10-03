using System.Runtime.InteropServices;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Input;

public enum ActionKind
{
    Focus,
    MovePointer,
    LeftClick,
    RightClick,
    KeyPress,

    /// <summary>Types text as unicode, one character at a time — no keyboard layout, no IME.</summary>
    TypeText,

    /// <summary>Mouse wheel at the pointer's position. Positive scrolls up.</summary>
    Scroll,

    /// <summary>Press, move, release — a real drag, not a teleport.</summary>
    Drag,
}

public sealed record ActionParams
{
    public int X { get; init; }

    public int Y { get; init; }

    /// <summary>
    /// The control this action names, when it names one. Present means "act on that control", which lets the
    /// UI Automation pattern path reach it directly instead of clicking its centre by coordinates.
    /// </summary>
    public string? ElementId { get; init; }

    /// <summary>Virtual-key code for <see cref="ActionKind.KeyPress"/>.</summary>
    public ushort VirtualKey { get; init; }

    /// <summary>Text for <see cref="ActionKind.TypeText"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Wheel notches for <see cref="ActionKind.Scroll"/>; positive is up.</summary>
    public int ScrollDelta { get; init; }

    /// <summary>Release point for <see cref="ActionKind.Drag"/>.</summary>
    public int ToX { get; init; }

    public int ToY { get; init; }

    public string Describe() => VirtualKey != 0
        ? $"vk={VirtualKey}"
        : !string.IsNullOrEmpty(Text)
            ? $"text={Text.Length} chars"
            : ScrollDelta != 0
                ? $"scroll={ScrollDelta}"
                : $"({X},{Y})";
}

public sealed record InjectionResult(bool Ok, uint ErrorCode, string Detail);

/// <summary>
/// Input injection via <c>SendInput</c> (design doc §2.6 L1). Every call reports whether Windows accepted
/// the event: a silently dropped input is the failure mode that makes desktop automation untrustworthy.
/// </summary>
public sealed class InputInjector
{
    /// <summary>
    /// The flags every <i>positional</i> mouse event needs.
    /// <para>
    /// <c>MOUSEEVENTF_ABSOLUTE</c> is not optional. Without it <c>dx</c>/<c>dy</c> are relative mickeys and
    /// <c>MOUSEEVENTF_VIRTUALDESK</c> is ignored entirely — so "click (800,600)" was sent as "move by
    /// 800,600", which lands in the corner of the screen from anywhere other than the origin.
    /// </para>
    /// </summary>
    private const uint PositionalFlags = NativeMethods.MouseeventfMove
        | NativeMethods.MouseeventfVirtualdesk
        | NativeMethods.MouseeventfAbsolute;

    private readonly Func<VirtualDesktop> _desktop;

    /// <param name="desktop">Overridable so the coordinate math can be exercised without a real display.</param>
    public InputInjector(Func<VirtualDesktop>? desktop = null) => _desktop = desktop ?? VirtualDesktop.Current;

    /// <summary>
    /// Screen point → the 0..65535 pair <c>SendInput</c> wants, or the reason it cannot be delivered.
    /// A point outside the virtual desktop is refused rather than clamped: silently turning "click the
    /// button" into "click the screen edge" is the failure this whole layer exists to avoid.
    /// </summary>
    private bool TryPosition(int x, int y, out int nx, out int ny, out string? error)
    {
        var desktop = _desktop();
        if (!desktop.Contains(x, y))
        {
            nx = ny = 0;
            error = $"({x},{y}) 不在虚拟桌面 {desktop} 内：这条路只能在屏幕上点，越界会被系统截到边缘";
            return false;
        }

        nx = desktop.NormalizeX(x);
        ny = desktop.NormalizeY(y);
        error = null;
        return true;
    }

    /// <summary>
    /// Brings a window to the foreground.
    /// <para>
    /// Plain <c>SetForegroundWindow</c> is refused when the caller is not already the foreground process — the
    /// Windows foreground lock — and typing is impossible without focus, so the refusal would make the whole
    /// input path unusable. Attaching to the foreground thread's input queue is the documented way around it:
    /// the two threads then share the right to set the foreground.
    /// </para>
    /// </summary>
    public InjectionResult Focus(nint handle)
    {
        if (handle == 0)
        {
            return new InjectionResult(false, 0, "no window handle");
        }

        var foreground = NativeMethods.GetForegroundWindow();

        // A minimized window can still be the foreground window — Windows keeps the handle after minimizing —
        // so "already foreground" must not short-circuit before the restore, or focusing it does nothing at all.
        if (foreground == handle && !NativeMethods.IsIconic(handle))
        {
            // …but "already foreground" is not "already reachable". A topmost window can sit over the active
            // one, and then the click the model is about to make lands on the cover. Raising within the
            // window's own band fixes that without changing whether it is always-on-top.
            Raise(handle);
            return new InjectionResult(true, 0, "already foreground (raised)");
        }

        // Attach to the *foreground* thread, not the target's: the right to set the foreground belongs to
        // whoever holds it now, and attaching to that queue is how a background process borrows it.
        var holder = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, 0);
        var current = NativeMethods.GetCurrentThreadId();
        var attached = holder != 0 && holder != current && NativeMethods.AttachThreadInput(current, holder, true);

        try
        {
            // A minimized window cannot be focused: SetForegroundWindow only lights up its taskbar button. And
            // while it is minimized its rect is a 160×28 stub at -32000,-32000, so every coordinate the model
            // derived from it would land in the void. Restoring is what "focus this window" means to a person.
            var restored = !NativeMethods.IsIconic(handle) || NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);

            NativeMethods.BringWindowToTop(handle);
            var ok = NativeMethods.SetForegroundWindow(handle);
            if (ok)
            {
                Raise(handle);
            }

            return new InjectionResult(
                ok,
                (uint)Marshal.GetLastWin32Error(),
                ok
                    ? $"foreground set{(attached ? " (attached)" : string.Empty)}{(restored ? string.Empty : ", restore failed")}"
                    : "SetForegroundWindow refused");
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(current, holder, false);
            }
        }
    }

    /// <summary>
    /// Puts the window at the top of its own z-order band without activating it, moving it or changing
    /// whether it is always-on-top. Focus is not only "give it the keyboard": a window can be the active one
    /// and still be covered, and then the click the caller makes next lands on the cover.
    /// </summary>
    private static void Raise(nint handle) =>
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTop,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);

    /// <summary>
    /// The records a pointer event would send, without sending them.
    /// <para>
    /// The flags and the normalized coordinates <i>are</i> the contract — and this is where they were wrong:
    /// every positional event was missing <c>MOUSEEVENTF_ABSOLUTE</c>, so a screen coordinate was applied as
    /// a mouse delta. Exposing the built records is what lets a test pin that down without moving anyone's
    /// cursor.
    /// </para>
    /// </summary>
    internal bool TryBuildPointer(
        int x,
        int y,
        uint buttonFlags,
        out NativeMethods.Input[] inputs,
        out string? error)
    {
        if (!TryPosition(x, y, out var nx, out var ny, out error))
        {
            inputs = [];
            return false;
        }

        inputs = [MouseAt(nx, ny, PositionalFlags | buttonFlags)];
        return true;
    }

    public InjectionResult MovePointer(int x, int y) => Positional(x, y, 0, $"move to ({x},{y})");

    public InjectionResult LeftClick(int x, int y) =>
        Positional(x, y, NativeMethods.MouseeventfLeftdown | NativeMethods.MouseeventfLeftup, $"left click at ({x},{y})");

    public InjectionResult RightClick(int x, int y) =>
        Positional(x, y, NativeMethods.MouseeventfRightdown | NativeMethods.MouseeventfRightup, $"right click at ({x},{y})");

    /// <summary>One positional event, refused before anything is injected when the point is off-screen.</summary>
    private InjectionResult Positional(int x, int y, uint buttonFlags, string detail) =>
        TryBuildPointer(x, y, buttonFlags, out var inputs, out var error)
            ? Send(inputs, detail)
            : new InjectionResult(false, 0, error!);

    /// <summary>
    /// Types text as unicode input. Character by character rather than by keyboard layout: the point is to
    /// send the text the model wrote, not whatever the user's layout would produce for those keys.
    /// </summary>
    public InjectionResult TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new InjectionResult(false, 0, "no text");
        }

        var inputs = new List<NativeMethods.Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(Keyboard(character, keyUp: false));
            inputs.Add(Keyboard(character, keyUp: true));
        }

        return Send(inputs.ToArray(), $"type {text.Length} chars");
    }

    private static NativeMethods.Input Keyboard(char character, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                ScanCode = character,
                Flags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0),
            },
        },
    };

    /// <summary>
    /// The wheel at the pointer's current position, measured in notches; positive is up.
    /// <para>
    /// A notch is not the number in <c>MOUSEINPUT.mouseData</c>. Windows expresses the wheel in units of
    /// <c>WHEEL_DELTA</c> (120), and a control accumulates what arrives until it has a whole unit — which is
    /// why <c>mouseData = 3</c> scrolls nothing at all, and why passing the model's number straight through
    /// turns "scroll down 3" into no movement with the injection still reporting success. The delta is scaled
    /// here, at the one place that knows the unit.
    /// </para>
    /// </summary>
    public InjectionResult Scroll(int delta) =>
        TryBuildWheel(delta, out var inputs, out var error)
            ? Send(inputs, $"scroll {delta} notches")
            : new InjectionResult(false, 0, error!);

    /// <summary>
    /// The records a wheel would send, without sending them. Exposed for the same reason as
    /// <see cref="TryBuildPointer"/>: the unit conversion is the contract, and asserting it on the record
    /// keeps the test off the user's scroll wheel.
    /// </summary>
    internal bool TryBuildWheel(int delta, out NativeMethods.Input[] inputs, out string? error)
    {
        inputs = [];

        if (delta == 0)
        {
            error = "no delta";
            return false;
        }

        // mouseData is a DWORD that Windows reads as a *signed* delta, so a negative count is legitimate —
        // scrolling down is the common case, not an error. What must be refused is a value that does not
        // fit: it would be truncated, and a truncated negative delta arrives as a scroll the other way.
        var wheel = (long)delta * WheelDelta;
        if (wheel is < int.MinValue or > int.MaxValue)
        {
            error = $"scroll {delta} notches is out of range";
            return false;
        }

        inputs =
        [
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        MouseData = (uint)wheel,
                        Flags = NativeMethods.MouseeventfWheel,
                    },
                },
            },
        ];

        error = null;
        return true;
    }

    /// <summary>
    /// One wheel notch, in the units <c>MOUSEINPUT.mouseData</c> is measured in. A control counts whole
    /// units of this, so a partial one is accumulated rather than acted on.
    /// </summary>
    internal const int WheelDelta = 120;

    /// <summary>
    /// The records a drag would send, without sending them — every waypoint absolute.
    /// <para>
    /// Press at one point, move to another, release, as several events: a single batch of down+move+up is not
    /// a drag to most applications, it is a click that moved.
    /// </para>
    /// </summary>
    internal bool TryBuildDrag(
        int fromX,
        int fromY,
        int toX,
        int toY,
        out NativeMethods.Input[] inputs,
        out string? error)
    {
        inputs = [];

        if (!TryPosition(fromX, fromY, out var fromNx, out var fromNy, out error))
        {
            return false;
        }

        if (!TryPosition(toX, toY, out var toNx, out var toNy, out error))
        {
            return false;
        }

        var desktop = _desktop();

        var steps = new List<NativeMethods.Input>
        {
            MouseAt(fromNx, fromNy, PositionalFlags | NativeMethods.MouseeventfLeftdown),
        };

        // The waypoints are interpolated in screen pixels — that is the path the caller described — and each
        // one is normalized on the way out. Both endpoints are inside the virtual desktop and that box is
        // convex, so every point between them is too.
        for (var step = 1; step <= 4; step++)
        {
            var x = fromX + (toX - fromX) * step / 4;
            var y = fromY + (toY - fromY) * step / 4;
            steps.Add(MouseAt(desktop.NormalizeX(x), desktop.NormalizeY(y), PositionalFlags));
        }

        steps.Add(MouseAt(toNx, toNy, PositionalFlags | NativeMethods.MouseeventfLeftup));

        inputs = steps.ToArray();
        return true;
    }

    public InjectionResult Drag(int fromX, int fromY, int toX, int toY) =>
        TryBuildDrag(fromX, fromY, toX, toY, out var inputs, out var error)
            ? Send(inputs, $"drag ({fromX},{fromY}) → ({toX},{toY})")
            : new InjectionResult(false, 0, error!);

    /// <summary>One absolute mouse event, already normalized by <see cref="TryPosition"/>.</summary>
    private static NativeMethods.Input MouseAt(int nx, int ny, uint flags) => new()
    {
        Type = NativeMethods.InputMouse,
        Union = new NativeMethods.InputUnion
        {
            Mouse = new NativeMethods.MouseInput { Dx = nx, Dy = ny, Flags = flags },
        },
    };

    public InjectionResult KeyPress(ushort virtualKey)
    {
        if (virtualKey == 0)
        {
            return new InjectionResult(false, 0, "no virtual key");
        }

        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputKeyboard,
                Union = new NativeMethods.InputUnion
                {
                    Keyboard = new NativeMethods.KeyboardInput { VirtualKey = virtualKey },
                },
            },
            new NativeMethods.Input
            {
                Type = NativeMethods.InputKeyboard,
                Union = new NativeMethods.InputUnion
                {
                    Keyboard = new NativeMethods.KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        Flags = NativeMethods.KeyeventfKeyup,
                    },
                },
            },
        };

        return Send(inputs, $"key {virtualKey}");
    }

    /// <summary>Size of one <c>INPUT</c> record as Windows expects it — asserted by the tests.</summary>
    public static int InputSize => Marshal.SizeOf<NativeMethods.Input>();

    private static InjectionResult Send(NativeMethods.Input[] inputs, string detail)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        var error = (uint)Marshal.GetLastWin32Error();

        return sent == inputs.Length
            ? new InjectionResult(true, 0, detail)
            : new InjectionResult(false, error, $"{detail}: SendInput delivered {sent}/{inputs.Length}");
    }
}
