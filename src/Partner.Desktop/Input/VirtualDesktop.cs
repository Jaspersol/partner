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
    /// <summary>Reads the current layout. Cheap (four <c>GetSystemMetrics</c> calls) but not cached: the user
    /// can unplug a monitor between two actions, and a stale origin would misplace everything after that.</summary>
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
    /// Windows does not divide the desktop into 65536 equal slices and round to the nearest one: it resolves
    /// a normalized coordinate back to a pixel with <c>floor(n × size / 65536)</c>. To land on a chosen pixel
    /// the normalized value therefore has to be the <i>smallest</i> n that resolves to it —
    /// <c>ceil(p × 65536 / size)</c> — and any other choice of n puts the pointer on a different pixel.
    /// </para>
    /// <para>
    /// The obvious alternative, scaling by <c>65535 / (size - 1)</c> so that both endpoints are inclusive,
    /// is the wrong model and it misplaces the pointer on a measurable number of pixels. Measured on a
    /// 2560-wide desktop: 8 of 2560 columns landed one pixel short (2→1, 4→3, 7→6, 9→8, 12→11, 17→16,
    /// 22→21, 27→26), and 5 of 1440 rows likewise. A 1 px error is invisible on a large target; on a 3 px
    /// wide control it is a different control, and the failure is silent — the injection reports success.
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

    /// <summary>
    /// The inverse of <see cref="Normalize(int, int, int)"/>: the pixel Windows resolves a normalized
    /// coordinate to. Exposed so the landing point is asserted rather than assumed.
    /// </summary>
    public static int Denormalize(ushort normalized, int origin, int size) =>
        size <= 1 ? origin : origin + (int)((normalized * (long)size) / 65536);

    public ushort NormalizeX(int x) => Normalize(x, X, Width);

    public ushort NormalizeY(int y) => Normalize(y, Y, Height);

    public override string ToString() => $"({X},{Y}) {Width}x{Height}";
}
