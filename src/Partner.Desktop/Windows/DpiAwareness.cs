using System.Runtime.InteropServices;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Windows;

/// <summary>
/// Per-monitor DPI awareness for the desktop host process.
/// <para>
/// Without it Windows virtualizes the whole coordinate space: <c>GetWindowRect</c>, DWM frame bounds and UI
/// Automation rectangles are all reported in a 96-dpi-scaled space, and GDI captures are upscaled to match.
/// The agent then reasons about a blurred picture at coordinates that no longer correspond to physical
/// pixels. Declaring per-monitor awareness makes every one of those APIs agree on real pixels.
/// </para>
/// <para>
/// The call is process-wide and one-shot: Windows fixes the mode the first time it is decided and refuses
/// every later attempt with <c>ERROR_ACCESS_DENIED</c>. A refusal therefore means "someone already decided",
/// not "this failed", and is reported as such.
/// </para>
/// </summary>
public static class DpiAwareness
{
    private const int ErrorAccessDenied = 5;

    private static readonly object Gate = new();
    private static string _outcome = "not attempted";

    /// <summary>What happened, for a log line. Never cleared: the mode cannot change within a process.</summary>
    public static string Outcome
    {
        get
        {
            lock (Gate)
            {
                return _outcome;
            }
        }
    }

    /// <summary>
    /// Declares per-monitor-v2 awareness, once, as early as possible — before any window, capture or UI
    /// Automation call. Idempotent: later calls return the first call's outcome.
    /// </summary>
    /// <returns>Whether the process is per-monitor aware, however that came about.</returns>
    public static bool EnsurePerMonitorV2()
    {
        lock (Gate)
        {
            if (_outcome != "not attempted")
            {
                return !_outcome.StartsWith("failed", StringComparison.Ordinal);
            }

            if (NativeMethods.SetProcessDpiAwarenessContext(new nint(NativeMethods.DpiAwarenessContextPerMonitorAwareV2)))
            {
                _outcome = "per-monitor-v2";
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAccessDenied)
            {
                // Already decided — by the host, or by a manifest, or by a CRT. Any of those is fine: the
                // point is that someone asked for per-monitor awareness before we did.
                _outcome = "already set by host or manifest";
                return true;
            }

            _outcome = $"failed: Win32 error {error}";
            return false;
        }
    }
}
