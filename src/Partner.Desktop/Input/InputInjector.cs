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

    /// <summary>
    /// Press and release twice, so Windows sees two clicks in time. Windows assembles a double click from two
    /// clicks in time; a control that opens on the second press does not see one click sent twice.
    /// </summary>
    DoubleClick,

    /// <summary>
    /// Scroll a control into view and stop there. A separate kind rather than a scroll with a guessed amount:
    /// only the control itself knows how far it needs to scroll.
    /// </summary>
    ScrollIntoView,
}

/// <summary>
/// A pointer position inside a control, as a fraction of its own size. Fractions because the caller knows the
/// control's rectangle, not its geometry. Both ends are inside the control by construction, so an offset can
/// never become a click somewhere else.
/// </summary>
public sealed record RelativePoint(double X, double Y)
{
    /// <summary>The control's centre — what an unspecified offset means.</summary>
    public static RelativePoint Centre { get; } = new(0.5, 0.5);

    /// <summary>Whether both axes are within the control.</summary>
    public bool IsInside => X is >= 0 and <= 1 && Y is >= 0 and <= 1;

    public override string ToString() => $"({X:0.###},{Y:0.###}) of the control";
}

public sealed record ActionParams
{
    public int X { get; init; }

    public int Y { get; init; }

    /// <summary>
    /// Where inside a named control to act, as a fraction of its size. Null means its centre — "somewhere in
    /// this control", never a corner of the screen.
    /// </summary>
    public RelativePoint? Within { get; init; }

    /// <summary>How many clicks: 1 or 2. Only meaningful for <see cref="ActionKind.LeftClick"/>.</summary>
    public int ClickCount { get; init; } = 1;

    /// <summary>
    /// The control this action names, when it names one. Present means "act on that control", which lets the
    /// UI Automation pattern path reach it directly instead of clicking its centre by coordinates.
    /// </summary>
    public string? ElementId { get; init; }

    /// <summary>Virtual-key code for <see cref="ActionKind.KeyPress"/>.</summary>
    public ushort VirtualKey { get; init; }

    /// <summary>Text for <see cref="ActionKind.TypeText"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Keys held down around the key being pressed, as virtual-key codes. Empty means none.</summary>
    public IReadOnlyList<ushort> Modifiers { get; init; } = Array.Empty<ushort>();

    /// <summary>Wheel notches for <see cref="ActionKind.Scroll"/>; positive is up.</summary>
    public int ScrollDelta { get; init; }

    /// <summary>Release point for <see cref="ActionKind.Drag"/>.</summary>
    public int ToX { get; init; }

    public int ToY { get; init; }

    public string Describe() => VirtualKey != 0
        ? $"vk={VirtualKey}{(Modifiers.Count == 0 ? string.Empty : $" + {string.Join("+", Modifiers)}")}"
        : !string.IsNullOrEmpty(Text)
            ? $"text={Text.Length} chars"
            : ScrollDelta != 0
                ? $"scroll={ScrollDelta}"
                : ClickCount == 2
                    ? $"double click ({X},{Y})"
                    : $"({X},{Y})";
}

public sealed record InjectionResult(bool Ok, uint ErrorCode, string Detail);

/// <summary>
/// Input injection via <c>SendInput</c>. Every call reports whether Windows accepted the event: a silently
/// dropped input is the failure mode that makes desktop automation untrustworthy.
/// </summary>
public sealed class InputInjector
{
    /// <summary>
    /// The flags every <i>positional</i> mouse event needs. Without <c>MOUSEEVENTF_ABSOLUTE</c>,
    /// <c>dx</c>/<c>dy</c> are relative mickeys and <c>MOUSEEVENTF_VIRTUALDESK</c> is ignored, so a screen
    /// coordinate would be applied as a relative move.
    /// </summary>
    private const uint PositionalFlags = NativeMethods.MouseeventfMove
        | NativeMethods.MouseeventfVirtualdesk
        | NativeMethods.MouseeventfAbsolute;

    private readonly Func<VirtualDesktop> _desktop;

    /// <param name="desktop">Overridable so the coordinate math can be exercised without a real display.</param>
    public InputInjector(Func<VirtualDesktop>? desktop = null) => _desktop = desktop ?? VirtualDesktop.Current;

    /// <summary>
    /// Screen point to the 0..65535 pair <c>SendInput</c> wants, or the reason it cannot be delivered. A point
    /// outside the virtual desktop is refused rather than clamped: silently turning a click into a screen-edge
    /// click is the failure this layer exists to avoid.
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
    /// Brings a window to the foreground. Plain <c>SetForegroundWindow</c> is refused when the caller is not
    /// already the foreground process (the foreground lock), and typing needs focus. Attaching to the
    /// foreground thread's input queue is the documented workaround: both threads then share the right to set
    /// the foreground.
    /// </summary>
    public InjectionResult Focus(nint handle)
    {
        if (handle == 0)
        {
            return new InjectionResult(false, 0, "no window handle");
        }

        var foreground = NativeMethods.GetForegroundWindow();

        // A minimized window can still be the foreground window — Windows keeps the handle after minimizing —
        // so this check must not short-circuit before the restore.
        if (foreground == handle && !NativeMethods.IsIconic(handle))
        {
            // Already foreground is not already reachable: a topmost window can cover the active one, and the
            // next click would land on the cover. Raising within the window's own band fixes that without
            // changing whether it is always-on-top.
            Raise(handle);
            return new InjectionResult(true, 0, "already foreground (raised)");
        }

        // Attach to the foreground thread, not the target's: the right to set the foreground belongs to
        // whoever holds it now.
        var holder = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, 0);
        var current = NativeMethods.GetCurrentThreadId();
        var attached = holder != 0 && holder != current && NativeMethods.AttachThreadInput(current, holder, true);

        try
        {
            // A minimized window cannot be focused: SetForegroundWindow only lights up its taskbar button.
            // While minimized its rect is a 160×28 stub at -32000,-32000, so every derived coordinate would
            // land in the void. Restoring is what "focus this window" means to a person.
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
    /// The most "focus" can mean without taking over the screen: a minimized window is restored without
    /// activation and sent behind the user's windows; a window already showing is left where it is.
    /// <para>
    /// There is no background equivalent of keyboard focus for a native window; the keyboard belongs to the
    /// foreground. The caller must not treat this as having given the window the keyboard.
    /// </para>
    /// </summary>
    public InjectionResult FocusInBackground(nint handle)
    {
        if (handle == 0)
        {
            return new InjectionResult(false, 0, "no window handle");
        }

        if (!NativeMethods.IsIconic(handle))
        {
            return new InjectionResult(true, 0, "bg.focus: 窗口没有最小化，前台没有动（后台模式下不会把它带到前台，也拿不到键盘焦点）");
        }

        NativeMethods.ShowWindow(handle, NativeMethods.SwShowNoActivate);

        // The restore puts the window at the top of the z-order, which is the very thing being avoided.
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndBottom,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);

        return NativeMethods.IsIconic(handle)
            ? new InjectionResult(false, 0, "bg.focus: 还原失败，窗口仍是最小化")
            : new InjectionResult(true, 0, "bg.focus: 已还原但没有激活，窗口放在其他窗口后面");
    }

    /// <summary>
    /// Puts the window at the top of its own z-order band without activating it, moving it or changing whether
    /// it is always-on-top. Focus is not only "give it the keyboard": an active window can still be covered,
    /// and then the next click lands on the cover.
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
    /// The records a pointer event would send, without sending them. Exposing the built records lets a test pin
    /// the flags and normalized coordinates without moving the cursor.
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

    public InjectionResult MovePointer(int x, int y) => Positional(x, y, 0, 0, $"move to ({x},{y})");

    public InjectionResult LeftClick(int x, int y) =>
        Positional(
            x, y,
            NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup,
            $"left click at ({x},{y})",
            count: 1);

    public InjectionResult RightClick(int x, int y) =>
        Positional(
            x, y,
            NativeMethods.MouseeventfRightdown, NativeMethods.MouseeventfRightup,
            $"right click at ({x},{y})",
            count: 1);

    /// <summary>A press and release, twice, separated by <see cref="DoubleClickDelayMs"/>.</summary>
    public InjectionResult DoubleClick(int x, int y) =>
        Positional(
            x, y,
            NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup,
            $"double click at ({x},{y})",
            count: 2);

    /// <summary>
    /// The records a click of a given count would send, without sending them. A double click is two complete
    /// down/up pairs separated by a pause, not one down/up/down/up: Windows assembles a double click from two
    /// clicks in time, and a control that opens on the second press does not see a four-event burst as one.
    /// The pause is explicit because the machine's own double-click speed is unknown to the model.
    /// </summary>
    internal bool TryBuildClick(
        int x,
        int y,
        uint downFlag,
        uint upFlag,
        int count,
        out NativeMethods.Input[] inputs,
        out string? error)
    {
        if (!TryPosition(x, y, out var nx, out var ny, out error))
        {
            inputs = [];
            return false;
        }

        if (count is < 1 or > 2)
        {
            inputs = [];
            error = $"click count {count} is not supported (1 = single, 2 = double)";
            return false;
        }

        var click = new List<NativeMethods.Input>
        {
            MouseAt(nx, ny, PositionalFlags | downFlag),
            MouseAt(nx, ny, PositionalFlags | upFlag),
        };

        if (count == 2)
        {
            click.Add(KeyboardDelay());
            click.Add(MouseAt(nx, ny, PositionalFlags | downFlag));
            click.Add(MouseAt(nx, ny, PositionalFlags | upFlag));
        }

        inputs = click.ToArray();
        error = null;
        return true;
    }

    /// <summary>
    /// The gap between the two clicks of a double click, in milliseconds. Comfortably inside the double-click
    /// time Windows recognises and outside the interval at which clicks read as separate presses.
    /// </summary>
    internal const int DoubleClickDelayMs = 60;

    /// <summary>A do-nothing keyboard event that exists only to spend time.</summary>
    private static NativeMethods.Input KeyboardDelay() => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput { ScanCode = 0xFFFF },
        },
    };

    /// <summary>One positional event, refused before anything is injected when the point is off-screen.</summary>
    private InjectionResult Positional(int x, int y, uint downFlag, uint upFlag, string detail, int count = 1)
    {
        if (!TryPosition(x, y, out var nx, out var ny, out var error))
        {
            return new InjectionResult(false, 0, error!);
        }

        var move = MouseAt(nx, ny, PositionalFlags);

        if (downFlag == 0)
        {
            // MovePointer: no button follows, so nothing to pace — send the move and be done.
            return Send([move], detail);
        }

        // The pause between arriving and pressing: the target needs time to turn the move into hover state.
        // Hover-driven targets — rows that reveal buttons on hover, menus, split entries — read an immediate
        // press as a click from no mouse. Moving first and dwelling is what a person's hand does.
        if (HoverUnsettled(x, y))
        {
            var positioned = Send([move], $"move to ({x},{y})");
            if (!positioned.Ok)
            {
                return positioned;
            }

            Thread.Sleep(HoverDwellMs);
        }

        var press = MouseAt(nx, ny, PositionalFlags | downFlag);
        var release = MouseAt(nx, ny, PositionalFlags | upFlag);

        if (count == 2)
        {
            // Two real sends with a real sleep between them: SendInput queues events back to back, so the
            // double click's spacing must be actual wall time.
            var first = Send([press, release], detail);
            if (!first.Ok)
            {
                return first;
            }

            Thread.Sleep(DoubleClickDelayMs);
            return Send([press, release], $"{detail} ×2");
        }

        return Send([press, release], detail);
    }

    /// <summary>The pause between arriving and pressing, in milliseconds — long enough for the target's
    /// message loop to turn the move into a hover, short enough that the human eye reads it as one motion.</summary>
    public const int HoverDwellMs = 40;

    /// <summary>
    /// Whether the pointer is somewhere other than the point: a click whose pointer is already there needs no
    /// artificial pause — the hover state is already settled. An unreadable position skips nothing.
    /// </summary>
    private static bool HoverUnsettled(int x, int y)
    {
        if (!NativeMethods.GetCursorPos(out var at))
        {
            return true;
        }

        return at.X != x || at.Y != y;
    }

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
    /// The wheel at the pointer's current position, in notches; positive is up. A notch is not the number in
    /// <c>MOUSEINPUT.mouseData</c>: Windows expresses the wheel in units of <c>WHEEL_DELTA</c> (120), and a
    /// control accumulates partial units. Passing the notch count straight through would scroll nothing while
    /// still reporting success, so the delta is scaled here.
    /// </summary>
    public InjectionResult Scroll(int delta) =>
        TryBuildWheel(delta, out var inputs, out var error)
            ? Send(inputs, $"scroll {delta} notches")
            : new InjectionResult(false, 0, error!);

    /// <summary>
    /// The records a wheel would send, without sending them. Exposed so the unit conversion can be asserted
    /// without touching the user's scroll wheel.
    /// </summary>
    internal bool TryBuildWheel(int delta, out NativeMethods.Input[] inputs, out string? error)
    {
        inputs = [];

        if (delta == 0)
        {
            error = "no delta";
            return false;
        }

        // mouseData is a DWORD that Windows reads as a signed delta, so a negative count is legitimate.
        // A value that does not fit must be refused: truncation would turn a scroll down into a scroll up.
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
    /// The records a drag would send, without sending them, with every waypoint absolute. Press at one point,
    /// move to another, release, as several events: a single batch of down+move+up reads as a click that moved,
    /// not a drag.
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

        // Waypoints are interpolated in screen pixels, the path the caller described, and normalized on the
        // way out. Both endpoints are inside the virtual desktop and that box is convex, so every point
        // between them is too.
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

    public InjectionResult KeyPress(ushort virtualKey, IReadOnlyList<ushort>? modifiers = null)
    {
        if (!TryBuildKeyPress(virtualKey, modifiers, out var inputs, out var error))
        {
            return new InjectionResult(false, 0, error!);
        }

        var held = modifiers is { Count: > 0 } ? $" with {string.Join("+", modifiers.Select(m => ModifierName(m)))}" : string.Empty;
        return Send(inputs, $"key {virtualKey}{held}");
    }

    /// <summary>
    /// The records a key press would send, without sending them. A modifier is not part of the key: Windows
    /// delivers "Ctrl+C" as C pressed while Ctrl is down, so a modifier is pressed first and released last.
    /// If SendInput drops part of the batch, the caller sees a failure rather than a modifier stuck down.
    /// Holding a modifier while pressing the same modifier is refused; holding one while pressing another is
    /// ordinary.
    /// </summary>
    internal bool TryBuildKeyPress(
        ushort virtualKey,
        IReadOnlyList<ushort>? modifiers,
        out NativeMethods.Input[] inputs,
        out string? error)
    {
        inputs = [];

        if (virtualKey == 0)
        {
            error = "no virtual key";
            return false;
        }

        var held = modifiers ?? Array.Empty<ushort>();

        if (held.Contains(virtualKey))
        {
            error = $"virtual key {virtualKey} is already held as a modifier";
            return false;
        }

        var batch = new List<NativeMethods.Input>(held.Count * 2 + 2);

        foreach (var modifier in held)
        {
            batch.Add(Key(modifier, keyUp: false));
        }

        batch.Add(Key(virtualKey, keyUp: false));
        batch.Add(Key(virtualKey, keyUp: true));

        // Released in reverse, so nested holds unwind the way they were stacked.
        for (var index = held.Count - 1; index >= 0; index--)
        {
            batch.Add(Key(held[index], keyUp: true));
        }

        inputs = batch.ToArray();
        error = null;
        return true;
    }

    /// <summary>Whether a virtual key is one of the four modifiers.</summary>
    internal static bool IsModifier(ushort virtualKey) => virtualKey is VirtualShift or VirtualControl or VirtualMenu or VirtualWin;

    /// <summary>The virtual keys that make the next key a chord.</summary>
    public const ushort VirtualShift = 0x10;
    public const ushort VirtualControl = 0x11;
    public const ushort VirtualMenu = 0x12;
    public const ushort VirtualWin = 0x5B;

    /// <summary>The names a model writes, mapped to the keys they mean.</summary>
    public static IReadOnlyDictionary<string, ushort> ModifierKeys { get; } = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = VirtualShift,
        ["ctrl"] = VirtualControl,
        ["control"] = VirtualControl,
        ["alt"] = VirtualMenu,
        ["menu"] = VirtualMenu,
        ["win"] = VirtualWin,
    };

    private static string ModifierName(ushort virtualKey) => virtualKey switch
    {
        VirtualShift => "shift",
        VirtualControl => "ctrl",
        VirtualMenu => "alt",
        VirtualWin => "win",
        _ => virtualKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static NativeMethods.Input Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? NativeMethods.KeyeventfKeyup : 0,
            },
        },
    };

    /// <summary>Size of one <c>INPUT</c> record as Windows expects it.</summary>
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
