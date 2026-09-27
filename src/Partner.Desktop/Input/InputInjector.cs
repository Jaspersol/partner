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
            return new InjectionResult(true, 0, "already foreground");
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

    public InjectionResult MovePointer(int x, int y)
    {
        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        Dx = x,
                        Dy = y,
                        Flags = NativeMethods.MouseeventfMove | NativeMethods.MouseeventfVirtualdesk,
                    },
                },
            },
        };

        return Send(inputs, $"move to ({x},{y})");
    }

    public InjectionResult LeftClick(int x, int y)
    {
        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        Dx = x,
                        Dy = y,
                        Flags = NativeMethods.MouseeventfMove | NativeMethods.MouseeventfVirtualdesk
                            | NativeMethods.MouseeventfLeftdown | NativeMethods.MouseeventfLeftup,
                    },
                },
            },
        };

        return Send(inputs, $"left click at ({x},{y})");
    }

    public InjectionResult RightClick(int x, int y)
    {
        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        Dx = x,
                        Dy = y,
                        Flags = NativeMethods.MouseeventfMove | NativeMethods.MouseeventfVirtualdesk
                            | NativeMethods.MouseeventfRightdown | NativeMethods.MouseeventfRightup,
                    },
                },
            },
        };

        return Send(inputs, $"right click at ({x},{y})");
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

    /// <summary>The wheel at the pointer's current position; positive notches scroll up.</summary>
    public InjectionResult Scroll(int delta)
    {
        if (delta == 0)
        {
            return new InjectionResult(false, 0, "no delta");
        }

        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        MouseData = unchecked((uint)delta),
                        Flags = NativeMethods.MouseeventfWheel,
                    },
                },
            },
        };

        return Send(inputs, $"scroll {delta}");
    }

    /// <summary>
    /// Press at one point, move to another, release. Sent as several events with a pause: a single batch of
    /// down+move+up is not a drag to most applications, it is a click that moved.
    /// </summary>
    public InjectionResult Drag(int fromX, int fromY, int toX, int toY)
    {
        var steps = new List<NativeMethods.Input>();
        steps.Add(Mouse(fromX, fromY, NativeMethods.MouseeventfMove | NativeMethods.MouseeventfVirtualdesk | NativeMethods.MouseeventfLeftdown));

        for (var step = 1; step <= 4; step++)
        {
            var x = fromX + (toX - fromX) * step / 4;
            var y = fromY + (toY - fromY) * step / 4;
            steps.Add(Mouse(x, y, NativeMethods.MouseeventfMove | NativeMethods.MouseeventfVirtualdesk));
        }

        steps.Add(Mouse(toX, toY, NativeMethods.MouseeventfMove | NativeMethods.MouseeventfVirtualdesk | NativeMethods.MouseeventfLeftup));

        return Send(steps.ToArray(), $"drag ({fromX},{fromY}) → ({toX},{toY})");
    }

    private static NativeMethods.Input Mouse(int x, int y, uint flags) => new()
    {
        Type = NativeMethods.InputMouse,
        Union = new NativeMethods.InputUnion
        {
            Mouse = new NativeMethods.MouseInput { Dx = x, Dy = y, Flags = flags },
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
