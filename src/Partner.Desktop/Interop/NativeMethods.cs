using System.Runtime.InteropServices;

namespace Partner.Desktop.Interop;

/// <summary>
/// The Win32 surface M9 needs (design doc §2.6 L1): window enumeration, foreground control, input
/// injection and GDI capture.
/// <para>
/// Capture uses GDI (<c>PrintWindow</c> with a screen-DC <c>BitBlt</c> fallback) rather than
/// <c>Windows.Graphics.Capture</c>: the .NET 8 Windows SDK projection exposes no
/// <c>GraphicsCaptureItem.CreateFromWindowId</c> / <c>Win32Interop</c>, so WGC would require hand-written
/// COM interop plus a D3D11 device — not justified while no consumer needs more than ~10 fps
/// (frame-level loops are explicitly out of scope, Phase 5c).
/// </para>
/// </summary>
internal static class NativeMethods
{
    // ---- window enumeration / geometry -----------------------------------------------------------
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextW(nint hWnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    /// <summary>
    /// A window's client area in screen coordinates — where its content starts.
    /// <para>
    /// The frame's top-left is not this: a window's content sits inside its borders and title bar, by an amount
    /// that varies with the theme and the DPI. A page reports its own elements relative to the viewport, which
    /// begins at the client area, so this is the one number that can convert between the two.
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hWnd, ref Point point);

    /// <summary>The window a point is delivered to, by the same hit-test the mouse uses.</summary>
    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    /// <summary>Walks up to an ancestor; <see cref="GaRoot"/> resolves a child control to its top-level window.</summary>
    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hWnd, uint flags);

    internal const uint GaRoot = 2;

    /// <summary>Walks the parent <i>and</i> owner chains: a menu or popup answers its owning window.</summary>
    internal const uint GaRootOwner = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>Whether the window is minimized. A minimized window's rect is a 160×28 stub at -32000,-32000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    /// <summary>Changes the window's show state; <see cref="SwRestore"/> un-minimizes it.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int command);

    internal const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    // The bounding box of every monitor in one coordinate space — the space SendInput's absolute mouse
    // coordinates are normalized against.
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out Rect value, int size);

    internal const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    // ---- capture --------------------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);

    internal const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint hdc, nint gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(nint destDc, int x, int y, int width, int height, nint srcDc, int srcX, int srcY, uint rop);

    internal const uint Srccopy = 0x00CC0020;

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(
        nint hdc,
        nint bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo info,
        uint usage);

    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    // ---- focus ----------------------------------------------------------------------------------

    /// <summary>
    /// Opts the process into per-monitor DPI awareness. Must be the process's first DPI decision: Windows
    /// fixes the mode once and answers <c>E_ACCESSDENIED</c> (returning false) to every later attempt.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(nint context);

    internal const int DpiAwarenessContextPerMonitorAwareV2 = -4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool BringWindowToTop(nint hwnd);

    /// <summary>Reorders a window in the z-order without moving, resizing or activating it.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Top of the z-order <i>within the window's own band</i> — it does not make a window topmost.</summary>
    internal static readonly nint HwndTop = 0;

    internal const uint SwpNosize = 0x0001;
    internal const uint SwpNomove = 0x0002;
    internal const uint SwpNoactivate = 0x0010;

    // ---- clipboard ------------------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetClipboardData(uint format, nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalFree(nint handle);

    // ---- input injection ------------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint MouseeventfMove = 0x0001;
    internal const uint MouseeventfLeftdown = 0x0002;
    internal const uint MouseeventfLeftup = 0x0004;
    internal const uint MouseeventfRightdown = 0x0008;
    internal const uint MouseeventfRightup = 0x0010;
    internal const uint MouseeventfWheel = 0x0800;
    internal const uint MouseeventfVirtualdesk = 0x4000;

    /// <summary>
    /// Makes <c>dx</c>/<c>dy</c> mean "a position in the 0..65535 coordinate range" instead of "how far to
    /// move". Required for <see cref="MouseeventfVirtualdesk"/> to have any effect: the documentation for
    /// that flag says it must be combined with this one, and without it a screen coordinate is applied as
    /// a relative mouse delta.
    /// </summary>
    internal const uint MouseeventfAbsolute = 0x8000;

    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint KeyeventfUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    // ---- token integrity level ------------------------------------------------------------------

    /// <summary>
    /// Reads the integrity level of the process owning a window, for the UIPI gate. Every call used here is
    /// documented against <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, so a query succeeds across integrity
    /// levels (that is the point of the limited right): what it cannot read is not "nobody may know the
    /// level" but a protected process, which the gate treats as undetermined rather than elevated.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, bool inherit, uint processId);

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint process, uint access, out nint token);

    internal const uint TokenQuery = 0x0008;
    internal const uint TokenIntegrityLevel = 25;

    /// <summary>
    /// Reads one token information class into an unmanaged buffer. Called first with a null buffer to size
    /// the answer, then with it — the standard two-step. Buffer is allocated by the caller with
    /// <c>Marshal.AllocHGlobal</c> because the SID pointer read out of it must stay in unmanaged memory for
    /// the <c>GetSidSubAuthority</c> calls that follow.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(nint token, uint class_, nint buffer, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    internal static extern nint GetSidSubAuthority(nint sid, uint index);

    [DllImport("advapi32.dll")]
    internal static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
