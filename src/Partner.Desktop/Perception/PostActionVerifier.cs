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

        if (after.Width != before.Width || after.Height != before.Height)
        {
            return VerifyResult.Indeterminate(
                "frame geometry changed between captures", watch.Elapsed.TotalMilliseconds, after.Method);
        }

        var dirty = FindDirtyBounds(before.Bgra, after.Bgra, after.Width, after.Height, watch, _budget);
        if (dirty is null)
        {
            return VerifyResult.Indeterminate(
                $"verification budget of {_budget.TotalMilliseconds:0}ms exceeded",
                watch.Elapsed.TotalMilliseconds,
                after.Method);
        }

        if (dirty.IsEmpty)
        {
            // Measured, and nothing moved anywhere in the frame.
            return new VerifyResult(
                VerifyDecision.NoChange, 0, after.Method, "no dirty region", watch.Elapsed.TotalMilliseconds);
        }

        var intersection = dirty.Intersect(target);
        var ratio = target.Area == 0 ? 0 : (double)intersection.Area / target.Area;

        var decision = ratio >= MinIntersectedAreaRatio ? VerifyDecision.AnyChange : VerifyDecision.NoChange;
        var reason = decision == VerifyDecision.AnyChange
            ? $"dirty region intersects {ratio:P2} of the target"
            : $"dirty region intersects only {ratio:P2} of the target";

        return new VerifyResult(decision, ratio, after.Method, reason, watch.Elapsed.TotalMilliseconds);
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
