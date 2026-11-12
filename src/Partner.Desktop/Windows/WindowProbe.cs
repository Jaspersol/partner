using System.Runtime.InteropServices;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Windows;

public enum WindowProbeStatus
{
    Ok,
    Gone,
    Unresponsive,
}

public readonly record struct WindowProbeResult(WindowProbeStatus Status, string? Detail)
{
    public bool Ok => Status == WindowProbeStatus.Ok;

    public static WindowProbeResult Fine { get; } = new(WindowProbeStatus.Ok, null);
}

/// <summary>
/// Whether a window still exists and whether its message loop answers.
/// <para>
/// UI Automation calls are cross-process RPCs with a provider on the other side. A window whose UI thread is
/// blocked cannot answer them; without this probe the first property read blocks until the client's internal
/// timeout, and every later node in the walk blocks again. <c>SendMessageTimeout(WM_NULL)</c> asks the same
/// question with a deadline of our choosing, before any UIA call is made.
/// </para>
/// </summary>
public static class WindowProbe
{
    /// <summary>Long enough for a busy but working window, short enough that a hung one is reported in time.</summary>
    public const uint DefaultTimeoutMs = 2000;

    public static WindowProbeResult Check(nint handle, uint timeoutMs = DefaultTimeoutMs)
    {
        if (handle == 0 || !NativeMethods.IsWindow(handle))
        {
            return new WindowProbeResult(WindowProbeStatus.Gone, "窗口已经不在了");
        }

        // SMTO_ABORTIFHUNG makes Windows answer immediately when the target is already marked hung; the
        // timeout bounds a target that is not marked yet but never pumps its queue.
        var answered = NativeMethods.SendMessageTimeoutW(
            handle,
            NativeMethods.WmNull,
            0,
            0,
            NativeMethods.SmtoAbortIfHung,
            timeoutMs,
            out _);

        if (answered != 0)
        {
            return WindowProbeResult.Fine;
        }

        var error = Marshal.GetLastWin32Error();
        if (!NativeMethods.IsWindow(handle))
        {
            return new WindowProbeResult(WindowProbeStatus.Gone, "窗口在探测期间关闭了");
        }

        return error == 0
            ? new WindowProbeResult(
                WindowProbeStatus.Unresponsive,
                $"窗口在 {timeoutMs}ms 内没有应答（UI 线程可能被阻塞）")
            : new WindowProbeResult(
                WindowProbeStatus.Unresponsive,
                $"窗口没有应答（Win32 错误 {error}），UI 线程可能被阻塞");
    }
}
