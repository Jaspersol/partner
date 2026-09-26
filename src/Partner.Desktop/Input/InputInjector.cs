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
}

public sealed record ActionParams
{
    public int X { get; init; }

    public int Y { get; init; }

    /// <summary>Virtual-key code for <see cref="ActionKind.KeyPress"/>.</summary>
    public ushort VirtualKey { get; init; }

    public string Describe() => VirtualKey != 0
        ? $"vk={VirtualKey}"
        : $"({X},{Y})";
}

public sealed record InjectionResult(bool Ok, uint ErrorCode, string Detail);

/// <summary>
/// Input injection via <c>SendInput</c> (design doc §2.6 L1). Every call reports whether Windows accepted
/// the event: a silently dropped input is the failure mode that makes desktop automation untrustworthy.
/// </summary>
public sealed class InputInjector
{
    public InjectionResult Focus(nint handle)
    {
        if (handle == 0)
        {
            return new InjectionResult(false, 0, "no window handle");
        }

        var ok = NativeMethods.SetForegroundWindow(handle);
        return new InjectionResult(ok, (uint)Marshal.GetLastWin32Error(), ok ? "foreground set" : "SetForegroundWindow refused");
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
