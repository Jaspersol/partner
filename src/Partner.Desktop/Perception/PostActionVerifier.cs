using System.Diagnostics;
using Partner.Desktop.Capture;

namespace Partner.Desktop.Perception;

public enum VerifyDecision
{
    /// <summary>Enough of the target region changed.</summary>
    AnyChange,

    /// <summary>Measured, and nothing (relevant) changed.</summary>
    NoChange,

    /// <summary>Could not be measured; never conflate this with "nothing changed".</summary>
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
/// Post-action verification: a delivered action never proves an outcome, so the region is re-observed and
/// compared within a fixed budget. The dirty rect is the bounding box of changed pixels, derived from the frame
/// diff because the GDI backend has no dirty-rect source. Repaints are asynchronous, so a NoChange is
/// re-measured a bounded number of times (see <see cref="DefaultRecheckRounds"/>).
/// </summary>
public sealed class PostActionVerifier
{
    /// <summary>Poll budget for one verification.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>Minimum intersected-area ratio for a change to count.</summary>
    public const double MinIntersectedAreaRatio = 0.005;

    /// <summary>
    /// The pause between re-captures when the first frame shows no change yet. Repaints are asynchronous: a busy
    /// compositor (Chromium) or an app that queues its <c>WM_PAINT</c> may not have drawn the effect within the
    /// few milliseconds between release and first capture, and reading that early frame as "nothing changed"
    /// makes a working click look like a failure and invites a retry.
    /// </summary>
    public static readonly TimeSpan DefaultRecheckDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Extra captures after the first, when the first shows no change. Each is a full diff against
    /// the before-frame, so a change that appears on any of them is reported on the round it appeared.</summary>
    public const int DefaultRecheckRounds = 3;

    private readonly IScreenCapture _capture;
    private readonly TimeSpan _budget;
    private readonly TimeSpan _recheckDelay;
    private readonly int _recheckRounds;

    public PostActionVerifier(IScreenCapture capture, TimeSpan? budget = null, int? recheckRounds = null, TimeSpan? recheckDelay = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _budget = budget ?? DefaultBudget;
        _recheckRounds = recheckRounds ?? DefaultRecheckRounds;
        _recheckDelay = recheckDelay ?? DefaultRecheckDelay;
    }

    public TimeSpan Budget => _budget;

    public int RecheckRounds => _recheckRounds;

    /// <summary>
    /// Re-captures the window and compares it against <paramref name="before"/>.
    /// </summary>
    /// <param name="target">
    /// The region to judge, in <b>frame</b> coordinates, not screen coordinates. The frames are captures of the
    /// window, so their origin is the window's top-left; a screen rectangle here would intersect two coordinate
    /// spaces and make the ratio meaningless.
    /// </param>
    public Task<VerifyResult> VerifyAsync(
        nint handle,
        PixelRect target,
        CaptureResult? before,
        CancellationToken ct = default) =>
        VerifyCoreAsync(handle, target, screenRegion: null, before, ct);

    /// <summary>
    /// Judges one region of the window, named in <b>screen</b> coordinates. This is the honest form for a region
    /// that came from an observation: a screen rectangle is what the element list and the model both speak, while
    /// the frames are cropped to their own origins.
    /// <para>
    /// The decision is a fraction of the judged region's area, so judging a whole window can make a small
    /// control's change too small to register.
    /// </para>
    /// </summary>
    public Task<VerifyResult> VerifyScreenRegionAsync(
        nint handle,
        PixelRect screenRegion,
        CaptureResult? before,
        CancellationToken ct = default,
        bool screenCaptureOk = false) =>
        VerifyCoreAsync(handle, target: null, screenRegion, before, ct, screenCaptureOk);

    private async Task<VerifyResult> VerifyCoreAsync(
        nint handle,
        PixelRect? target,
        PixelRect? screenRegion,
        CaptureResult? before,
        CancellationToken ct,
        bool screenCaptureOk = false)
    {
        var watch = Stopwatch.StartNew();

        if (before is null || !before.Ok)
        {
            return VerifyResult.Indeterminate("no before frame", watch.Elapsed.TotalMilliseconds);
        }

        // Capturing just the region is much cheaper than PrintWindow, which renders the whole window through
        // DWM twice per action. "Safe" means nothing else can be drawn inside the region: a screen BitBlt copies
        // whatever is at those pixels, including a window on top, which would read as a change the target never
        // made. The caller must say the target is in front (the occlusion guard establishes that for a
        // coordinate action); otherwise this falls back to the window capture, which renders the window's own
        // contents regardless of what covers it. Both frames of one round must be in one coordinate space, so
        // the region-before-frame is cropped once and every round runs in the same mode.
        var regionMode = screenCaptureOk && screenRegion is { } region && before.ScreenRect is { } origin
            && Crop(before, region.X - origin.X, region.Y - origin.Y, region.Width, region.Height) is { } frame
            ? (Before: (CaptureResult?)frame, Screen: (PixelRect?)region)
            : (Before: (CaptureResult?)null, Screen: (PixelRect?)null);

        CaptureResult RoundBefore() => regionMode.Before ?? before;

        CaptureResult? captureFailure = null;

        async Task<(CaptureResult Frame, PixelRect? Judged)?> RoundAfterAsync()
        {
            if (regionMode.Before is not null && regionMode.Screen is { } region)
            {
                var patch = await _capture
                    .CaptureScreenRegionAsync(region.X, region.Y, region.Width, region.Height, ct)
                    .ConfigureAwait(false);

                if (patch.Ok)
                {
                    // Both frames are now the region itself, so the judged area is all of it.
                    return (patch with { ScreenRect = region }, null);
                }
            }

            var after = await _capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false);
            if (!after.Ok)
            {
                captureFailure ??= after;
                return null;
            }

            return (after, Region(target, screenRegion, before.ScreenRect));
        }

        var round = await RoundAfterAsync().ConfigureAwait(false);
        if (round is null)
        {
            return VerifyResult.Indeterminate(
                $"capture unavailable: {captureFailure?.Error ?? "no frame"}",
                watch.Elapsed.TotalMilliseconds,
                captureFailure?.Method ?? before.Method);
        }

        var elapsedMs = () => watch.Elapsed.TotalMilliseconds;
        var result = Compare(RoundBefore(), round.Value.Frame, round.Value.Judged, new Stopwatch(), elapsedMs);

        // Repaints are asynchronous. A NoChange measured within a few milliseconds of the release can be a frame
        // the application has not drawn yet, and reporting it as "nothing changed" invites a retry, which is how
        // a toggle gets double-triggered. A NoChange is re-measured on a short loop; the first round that sees a
        // change wins. A later failed capture, or an exhausted loop, leaves the measured NoChange standing.
        for (var recheck = 0; recheck < _recheckRounds && result.Decision == VerifyDecision.NoChange; recheck++)
        {
            await Task.Delay(_recheckDelay, ct).ConfigureAwait(false);
            var next = await RoundAfterAsync().ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            // One diff budget per round, not per verification: the budget bounds one scan from capture to
            // decision, and a scan sharing its clock with earlier rounds would answer Indeterminate immediately.
            result = Compare(RoundBefore(), next.Value.Frame, next.Value.Judged, new Stopwatch(), elapsedMs);
        }

        return result;
    }

    /// <summary>
    /// Compares two frames and judges <paramref name="frameRegion"/> of the overlap. Shared by both capture
    /// strategies so the decision itself is identical however the pixels were obtained.
    /// </summary>
    /// <param name="watch">The scan's own clock: the diff budget is per scan, and the round loop passes a
    /// fresh one. The reported elapsed is <paramref name="elapsedMs"/>'s — the whole verification.</param>
    private VerifyResult Compare(
        CaptureResult before,
        CaptureResult after,
        PixelRect? frameRegion,
        Stopwatch watch,
        Func<double>? elapsedMs = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // A window that was restored, moved or resized changes the frame between the two captures, and for a
        // focus that restored it, that change is the outcome being asked about. The overlap is a real
        // comparison, so compare that instead of reporting Indeterminate.
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);

        if (width <= 0 || height <= 0)
        {
            return VerifyResult.Indeterminate(
                "the two captures do not overlap at all",
                elapsedMs?.Invoke() ?? watch.Elapsed.TotalMilliseconds,
                after.Method);
        }

        var resized = after.Width != before.Width || after.Height != before.Height;
        var beforePixels = resized ? CropTopLeft(before.Bgra, before.Width, width, height) : before.Bgra;
        var afterPixels = resized ? CropTopLeft(after.Bgra, after.Width, width, height) : after.Bgra;

        var dirty = FindDirtyBounds(beforePixels, afterPixels, width, height, watch, _budget);
        if (dirty is null)
        {
            return VerifyResult.Indeterminate(
                $"verification budget of {_budget.TotalMilliseconds:0}ms exceeded",
                elapsedMs?.Invoke() ?? watch.Elapsed.TotalMilliseconds,
                after.Method);
        }

        var resizedNote = resized
            ? $"; window resized {before.Width}×{before.Height} → {after.Width}×{after.Height}, compared the overlap"
            : string.Empty;

        if (dirty.IsEmpty)
        {
            if (resized)
            {
                // The overlapping pixels are identical, but the window changed shape, which is itself something
                // that happened and must not be hidden behind "nothing changed".
                return new VerifyResult(
                    VerifyDecision.AnyChange,
                    1,
                    after.Method,
                    $"window resized {before.Width}×{before.Height} → {after.Width}×{after.Height}",
                    elapsedMs?.Invoke() ?? watch.Elapsed.TotalMilliseconds);
            }

            // Measured, and nothing moved anywhere in the frame.
            return new VerifyResult(
                VerifyDecision.NoChange, 0, after.Method, "no dirty region", elapsedMs?.Invoke() ?? watch.Elapsed.TotalMilliseconds);
        }

        // Both capture strategies arrive here with the judged region already in frame coordinates.
        var judged = frameRegion ?? new PixelRect(0, 0, width, height);
        var clipped = judged.Intersect(new PixelRect(0, 0, width, height));
        var intersection = dirty.Intersect(clipped);
        var ratio = clipped.Area == 0 ? 0 : (double)intersection.Area / clipped.Area;

        var decision = ratio >= MinIntersectedAreaRatio ? VerifyDecision.AnyChange : VerifyDecision.NoChange;
        var reason = decision == VerifyDecision.AnyChange
            ? $"dirty region intersects {ratio:P2} of the target{resizedNote}"
            : $"dirty region intersects only {ratio:P2} of the target{resizedNote}";

        return new VerifyResult(decision, ratio, after.Method, reason, elapsedMs?.Invoke() ?? watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// A rectangle cut out of a frame, in the frame's own pixels. Refused rather than shrunk when the rectangle
    /// is not inside the frame: a capture whose pixels do not match its region is the misalignment this exists
    /// to avoid.
    /// </summary>
    internal static CaptureResult? Crop(CaptureResult frame, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > frame.Width || y + height > frame.Height)
        {
            return null;
        }

        if (x == 0 && y == 0 && width == frame.Width && height == frame.Height)
        {
            return frame;
        }

        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(
                frame.Bgra,
                (((y + row) * frame.Width) + x) * 4,
                pixels,
                row * width * 4,
                width * 4);
        }

        return frame with { Width = width, Height = height, Bgra = pixels };
    }

    /// <summary>
    /// Where a region lands inside a frame. A screen region needs the frame's own origin; a region already in
    /// frame coordinates is taken as is. Null when the region cannot be placed, and the caller falls back to
    /// judging the whole frame.
    /// </summary>
    internal static PixelRect? Region(PixelRect? frameRegion, PixelRect? screenRegion, PixelRect? frameScreenRect)
    {
        if (frameRegion is { } given)
        {
            return given;
        }

        if (screenRegion is not { } screen || frameScreenRect is not { } origin)
        {
            return null;
        }

        return new PixelRect(screen.X - origin.X, screen.Y - origin.Y, screen.Width, screen.Height);
    }

    /// <summary>
    /// The top-left <paramref name="width"/>×<paramref name="height"/> corner of a frame, re-strided to the new
    /// width. A row-major frame carries its own stride, so cropping is a row copy; slicing the array would shear
    /// the picture.
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
