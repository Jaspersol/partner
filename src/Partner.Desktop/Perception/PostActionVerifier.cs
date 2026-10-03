using System.Diagnostics;
using Partner.Desktop.Capture;

namespace Partner.Desktop.Perception;

public enum VerifyDecision
{
    /// <summary>Enough of the target region changed.</summary>
    AnyChange,

    /// <summary>Measured, and nothing (relevant) changed.</summary>
    NoChange,

    /// <summary>Could not be measured — never conflate this with "nothing changed" (design doc §2.6.1 ②).</summary>
    Indeterminate,
}

public sealed record VerifyResult(
    VerifyDecision Decision,
    double IntersectedAreaRatio,
    string Source,
    string? Reason,
    double ElapsedMs)
{
    public static VerifyResult Indeterminate(string reason, double elapsedMs, string source = "gdi.diff") =>
        new(VerifyDecision.Indeterminate, 0, source, reason, elapsedMs);
}

/// <summary>
/// Post-action verification (design doc §2.6.1 ②). A delivered action never proves an outcome: the region
/// must be re-observed and compared within a fixed budget.
/// <para>
/// Adaptation recorded deliberately: the reference implementation reads DXGI dirty rects; here the dirty
/// rect is <i>derived from the frame diff</i> (bounding box of changed pixels) because the GDI backend has
/// no dirty-rect source. The decision matrix, the 0.005 area-ratio threshold and the 100 ms budget are
/// otherwise identical.
/// </para>
/// </summary>
public sealed class PostActionVerifier
{
    /// <summary>Poll budget for one verification (reference value: 100 ms).</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>Minimum intersected-area ratio for a change to count (reference value: 0.005).</summary>
    public const double MinIntersectedAreaRatio = 0.005;

    private readonly IScreenCapture _capture;
    private readonly TimeSpan _budget;

    public PostActionVerifier(IScreenCapture capture, TimeSpan? budget = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _budget = budget ?? DefaultBudget;
    }

    public TimeSpan Budget => _budget;

    /// <summary>
    /// Re-captures the window and compares it against <paramref name="before"/>.
    /// </summary>
    /// <param name="target">
    /// The region to judge, in <b>frame</b> coordinates — not screen coordinates. The frames are captures of
    /// the window, so their origin is the window's top-left corner; passing a screen rectangle here
    /// intersected two different coordinate spaces and made the ratio meaningless. The frame's own rectangle
    /// is what a window-level action means.
    /// </param>
    public async Task<VerifyResult> VerifyAsync(
        nint handle,
        PixelRect target,
        CaptureResult? before,
        CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();

        if (before is null || !before.Ok)
        {
            return VerifyResult.Indeterminate("no before frame", watch.Elapsed.TotalMilliseconds);
        }

        var after = await _capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false);
        if (!after.Ok)
        {
            return VerifyResult.Indeterminate(
                $"capture unavailable: {after.Error}", watch.Elapsed.TotalMilliseconds, after.Method);
        }

        // A window that was restored, moved or resized changes the frame between the two captures — and for a
        // focus that restored it, that change *is* the outcome being asked about. Reporting Indeterminate there
        // left the model with no signal about the action it had just taken, which is how a turn ends up
        // re-observing instead of proceeding. The overlap is still a real comparison, so compare that.
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);

        if (width <= 0 || height <= 0)
        {
            return VerifyResult.Indeterminate(
                "the two captures do not overlap at all", watch.Elapsed.TotalMilliseconds, after.Method);
        }

        var resized = after.Width != before.Width || after.Height != before.Height;
        var beforePixels = resized ? CropTopLeft(before.Bgra, before.Width, width, height) : before.Bgra;
        var afterPixels = resized ? CropTopLeft(after.Bgra, after.Width, width, height) : after.Bgra;

        var dirty = FindDirtyBounds(beforePixels, afterPixels, width, height, watch, _budget);
        if (dirty is null)
        {
            return VerifyResult.Indeterminate(
                $"verification budget of {_budget.TotalMilliseconds:0}ms exceeded",
                watch.Elapsed.TotalMilliseconds,
                after.Method);
        }

        var resizedNote = resized
            ? $"; window resized {before.Width}×{before.Height} → {after.Width}×{after.Height}, compared the overlap"
            : string.Empty;

        if (dirty.IsEmpty)
        {
            if (resized)
            {
                // The pixels that overlap are identical, but the window changed shape between the two captures
                // — which is itself something that happened, and hiding it behind "nothing changed" would be a
                // lie about a change the model can see.
                return new VerifyResult(
                    VerifyDecision.AnyChange,
                    1,
                    after.Method,
                    $"window resized {before.Width}×{before.Height} → {after.Width}×{after.Height}",
                    watch.Elapsed.TotalMilliseconds);
            }

            // Measured, and nothing moved anywhere in the frame.
            return new VerifyResult(
                VerifyDecision.NoChange, 0, after.Method, "no dirty region", watch.Elapsed.TotalMilliseconds);
        }

        var clipped = target.Intersect(new PixelRect(0, 0, width, height));
        var intersection = dirty.Intersect(clipped);
        var ratio = clipped.Area == 0 ? 0 : (double)intersection.Area / clipped.Area;

        var decision = ratio >= MinIntersectedAreaRatio ? VerifyDecision.AnyChange : VerifyDecision.NoChange;
        var reason = decision == VerifyDecision.AnyChange
            ? $"dirty region intersects {ratio:P2} of the target{resizedNote}"
            : $"dirty region intersects only {ratio:P2} of the target{resizedNote}";

        return new VerifyResult(decision, ratio, after.Method, reason, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// The top-left <paramref name="width"/>×<paramref name="height"/> corner of a frame, re-strided to the new
    /// width. A row-major frame carries its own stride, so cropping is a row copy — slicing the array would
    /// shear the picture.
    /// </summary>
    private static byte[] CropTopLeft(byte[] frame, int frameWidth, int width, int height)
    {
        var cropped = new byte[width * height * 4];
        var sourceStride = frameWidth * 4;
        var destinationStride = width * 4;

        for (var y = 0; y < height; y++)
        {
            Array.Copy(frame, y * sourceStride, cropped, y * destinationStride, destinationStride);
        }

        return cropped;
    }

    /// <summary>
    /// Bounding box of pixels that differ by more than <paramref name="channelThreshold"/> in any channel.
    /// Returns null when the budget runs out (caller reports <see cref="VerifyDecision.Indeterminate"/>).
    /// </summary>
    public static PixelRect? FindDirtyBounds(
        byte[] before,
        byte[] after,
        int width,
        int height,
        Stopwatch watch,
        TimeSpan budget,
        byte channelThreshold = 8)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var stride = width * 4;
        if (before.Length < stride * height || after.Length < stride * height)
        {
            return null;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        for (var y = 0; y < height; y++)
        {
            // Check the budget once per row: cheap, and bounds the worst case to one row of overrun.
            if ((y & 0x0F) == 0 && watch.Elapsed > budget)
            {
                return null;
            }

            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = row + (x * 4);
                if (Math.Abs(before[index] - after[index]) <= channelThreshold
                    && Math.Abs(before[index + 1] - after[index + 1]) <= channelThreshold
                    && Math.Abs(before[index + 2] - after[index + 2]) <= channelThreshold)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        return new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
