using System.Diagnostics;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Windows;

/// <summary>One observable top-level window (design doc §2.6 L1: 窗口枚举).</summary>
public sealed record DesktopWindow
{
    public required nint Handle { get; init; }

    public required string Title { get; init; }

    public required int ProcessId { get; init; }

    public string? ProcessName { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool IsForeground { get; init; }

    /// <summary>
    /// Whether the window is minimized. Its rect is then a 160×28 stub at -32000,-32000, which is a real trap:
    /// it looks like a window you can click, and clicking it lands in the void.
    /// </summary>
    public bool IsMinimized { get; init; }

    public override string ToString() => $"{Title} [{ProcessName ?? ProcessId.ToString()}] {Width}x{Height}";
}

/// <summary>Enumerates visible top-level windows. Read-only; safe to call at any time.</summary>
public static class WindowEnumerator
{
    public static IReadOnlyList<DesktopWindow> List()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<DesktopWindow>();

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle))
            {
                return true;
            }

            var title = GetTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                // Untitled windows (tooltips, shell internals) are not actionable targets.
                return true;
            }

            var bounds = GetBounds(handle);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            windows.Add(new DesktopWindow
            {
                Handle = handle,
                Title = title,
                ProcessId = (int)processId,
                ProcessName = TryGetProcessName(processId),
                X = bounds.Left,
                Y = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                IsForeground = handle == foreground,
                IsMinimized = NativeMethods.IsIconic(handle),
            });

            return true;
        }, 0);

        return windows;
    }

    /// <summary>Finds a window by process name (case-insensitive, extension optional) and/or title substring.</summary>
    public static DesktopWindow? Find(string? processName = null, string? titleContains = null) =>
        List().FirstOrDefault(w =>
            (processName is null
                || string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(w.ProcessName ?? string.Empty),
                    System.IO.Path.GetFileNameWithoutExtension(processName),
                    StringComparison.OrdinalIgnoreCase))
            && (titleContains is null || w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Visible bounds, preferring the DWM extended frame bounds: <c>GetWindowRect</c> includes the
    /// invisible resize border on Windows 10+, which would offset every captured pixel.
    /// </summary>
    internal static NativeMethods.Rect GetBounds(nint handle)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                handle,
                NativeMethods.DwmwaExtendedFrameBounds,
                out var extended,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>()) == 0
            && extended.Width > 0
            && extended.Height > 0)
        {
            return extended;
        }

        NativeMethods.GetWindowRect(handle, out var rect);
        return rect;
    }

    internal static string GetTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLengthW(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowTextW(handle, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    private static string? TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
