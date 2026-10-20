using Partner.Desktop.Interop;

namespace Partner.Desktop.Input;

/// <summary>
/// The virtual desktop: the bounding box of every monitor, in one coordinate space. This is the space
/// <c>SendInput</c> absolute mouse coordinates are expressed in — and therefore the space every screen
/// coordinate a caller passes to <see cref="InputInjector"/> is in.
/// </summary>
/// <param name="X">Left edge; negative when a monitor sits left of the primary.</param>
/// <remarks>
/// The origin is not always (0,0) and the size is not the primary monitor: with a second display to the left
/// or above, the origin is negative, and normalizing against the primary's size would shift every click.
/// </remarks>
public sealed record VirtualDesktop(int X, int Y, int Width, int Height)
{
    /// <summary>Reads the current layout. Not cached: a monitor can be unplugged between two actions, and a
    /// stale origin would misplace everything after that.</summary>
    public static VirtualDesktop Current()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);

        // A session with no interactive desktop (a service, or a disconnected RDP session) reports zeroes.
        // 1×1 keeps the normalization arithmetic total instead of dividing by zero.
        return width > 0 && height > 0
            ? new VirtualDesktop(x, y, width, height)
            : new VirtualDesktop(0, 0, 1, 1);
    }

    /// <summary>Whether a point can be delivered by <c>SendInput</c> at all.</summary>
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    /// <summary>
    /// Screen pixel to the 0..65535 range absolute mouse coordinates use.
    /// <para>
    /// Windows resolves a normalized coordinate back to a pixel with <c>floor(n × size / 65536)</c>. To land on
    /// a chosen pixel, n must be the smallest value that resolves to it: <c>ceil(p × 65536 / size)</c>. Any
    /// other choice puts the pointer on a different pixel, silently, while injection still reports success.
    /// </para>
    /// </summary>
    public static ushort Normalize(int value, int origin, int size)
    {
        if (size <= 1)
        {
            return 0;
        }

        var scaled = (long)Math.Ceiling((value - origin) * 65536.0 / size);
        return (ushort)Math.Clamp(scaled, 0, 65535);
    }

    /// <summary>Inverse of <see cref="Normalize(int, int, int)"/>: the pixel Windows resolves a normalized
    /// coordinate to.</summary>
    public static int Denormalize(ushort normalized, int origin, int size) =>
        size <= 1 ? origin : origin + (int)((normalized * (long)size) / 65536);

    public ushort NormalizeX(int x) => Normalize(x, X, Width);

    public ushort NormalizeY(int y) => Normalize(y, Y, Height);

    public override string ToString() => $"({X},{Y}) {Width}x{Height}";
}
