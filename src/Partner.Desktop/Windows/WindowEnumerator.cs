using System.Diagnostics;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Windows;

/// <summary>One observable top-level window.</summary>
public sealed record DesktopWindow
{
    public required nint Handle { get; init; }

    public required string Title { get; init; }

    public required int ProcessId { get; init; }

    public string? ProcessName { get; init; }

    /// <summary>
    /// When the owning process started, in UTC ticks — part of the window's <i>identity</i>.
    /// <para>
    /// Windows recycles window handles: the same handle can name a different window once the first one closes,
    /// and a process that restarted keeps neither its pid nor its start time. Pairing the handle with these
    /// distinguishes "the same window, moved" from "a different window, same id".
    /// Null when the process cannot be queried (an elevated process refuses).
    /// </para>
    /// </summary>
    public long? ProcessStartTicks { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool IsForeground { get; init; }

    /// <summary>
    /// Whether the window is minimized. Its rect is then a 160×28 stub at -32000,-32000: it looks like a window
    /// you can click, and clicking it lands in the void.
    /// </summary>
    public bool IsMinimized { get; init; }

    public override string ToString() => $"{Title} [{ProcessName ?? ProcessId.ToString()}] {Width}x{Height}";
}

/// <summary>
/// What a window was, at one instant: handle, process, process start time and visible rectangle. Two reads
/// that differ mean a capture or comparison spans a change that would misalign its pixels.
/// </summary>
public readonly record struct WindowIdentity(
    nint Handle,
    int ProcessId,
    long? ProcessStartTicks,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>Enumerates visible top-level windows. Read-only; safe to call at any time.</summary>
public static class WindowEnumerator
{
    /// <summary>The window's identity now, or null when it no longer exists.</summary>
    public static WindowIdentity? IdentityOf(nint handle)
    {
        if (handle == 0 || !NativeMethods.IsWindow(handle))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        var bounds = GetBounds(handle);

        return new WindowIdentity(
            handle,
            (int)processId,
            TryGetProcessStartTicks(processId),
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height);
    }
    public static IReadOnlyList<DesktopWindow> List()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<DesktopWindow>();

        // One process object per pid for the whole enumeration: each fact needs its own Process handle, and
        // resolving them per window opens one per window for the same handful of processes.
        var names = new Dictionary<uint, string?>();
        var starts = new Dictionary<uint, long?>();

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || IsCloaked(handle))
            {
                // Cloaked windows belong to another virtual desktop, or to a UWP app that is suspended. They
                // are not on the screen the model is looking at and cannot be clicked.
                return true;
            }

            // A tool window with an owner is a palette or a find bar that belongs to an application already in
            // the list; one without an owner is a floating tool of its own and is not a target.
            if (IsUnownedToolWindow(handle))
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

            // A UWP app is hosted in ApplicationFrameHost.exe: the frame is the window on screen, but the
            // process the model needs is the app inside it. Otherwise "设置" is reported as the host and a
            // debug port or an elevation check is aimed at the wrong process.
            if (string.Equals(GetClass(handle), "ApplicationFrameWindow", StringComparison.Ordinal))
            {
                var hosted = NativeMethods.FindWindowExW(handle, 0, "Windows.UI.Core.CoreWindow", null);
                if (hosted != 0)
                {
                    NativeMethods.GetWindowThreadProcessId(hosted, out var hostedProcess);
                    if (hostedProcess != 0)
                    {
                        processId = hostedProcess;
                    }
                }
            }

            windows.Add(new DesktopWindow
            {
                Handle = handle,
                Title = title,
                ProcessId = (int)processId,
                ProcessName = Cached(names, processId, TryGetProcessName),
                ProcessStartTicks = Cached(starts, processId, TryGetProcessStartTicks),
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

    /// <summary>
    /// The rectangle <c>GetWindowRect</c> reports: the visible frame expanded by the invisible resize border
    /// on Windows 10+. Needed by the capture, because <c>PrintWindow</c> renders from <i>this</i> origin.
    /// </summary>
    internal static NativeMethods.Rect GetOuterBounds(nint handle)
    {
        NativeMethods.GetWindowRect(handle, out var rect);
        return rect;
    }

    private static bool IsCloaked(nint handle) =>
        NativeMethods.DwmGetWindowAttributeInt(
            handle,
            NativeMethods.DwmwaCloaked,
            out var cloaked,
            sizeof(int)) == 0
        && cloaked != 0;

    private static bool IsUnownedToolWindow(nint handle) =>
        (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExstyle) & NativeMethods.WsExToolwindow) != 0
        && NativeMethods.GetWindow(handle, NativeMethods.GwOwner) == 0;

    internal static string GetClass(nint handle)
    {
        var buffer = new char[256];
        var copied = NativeMethods.GetClassNameW(handle, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
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

    /// <summary>
    /// One lookup per process per enumeration.
    /// <para>
    /// A null result is cached too: "this process refuses to answer" costs a handle to find out, so asking
    /// again per window multiplies process handles. <c>TryGetValue</c> distinguishes "cached as null" from
    /// "not cached", so an unconstrained <typeparamref name="T"/> is safe here and lets both facts share it.
    /// </para>
    /// </summary>
    private static T Cached<T>(Dictionary<uint, T> cache, uint processId, Func<uint, T> read)
    {
        if (!cache.TryGetValue(processId, out var value))
        {
            value = read(processId);
            cache[processId] = value;
        }

        return value;
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

    /// <summary>
    /// The owning process's start time, as UTC ticks. Part of the window's identity because a handle alone is
    /// not: Windows recycles handles, and a restarted process reuses its name and often its pid range.
    /// </summary>
    internal static long? TryGetProcessStartTicks(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // An elevated process refuses StartTime to a non-elevated caller; the window is still observable,
            // it just cannot be told apart from a recycled handle by time.
            return null;
        }
    }
}
