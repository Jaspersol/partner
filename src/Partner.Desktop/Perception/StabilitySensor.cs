using System.Security.Cryptography;
using Partner.Desktop.Capture;

namespace Partner.Desktop.Perception;

/// <summary>
/// Watches one window until its pixels stop moving: a slow dialog, a loading pane, a spinner. Waiting is one
/// call with a deadline, not repeated observations.
/// <para>
/// Stability is <b>two consecutive identical frames</b> spaced a hard delay apart, not one: a frame sampled
/// mid-repaint looks still, and a frame sampled right after the first click looks still about 30% of the time.
/// Two hash-identical rounds in a row are the pause a human would call "it settled".
/// </para>
/// </summary>
public sealed class StabilitySensor(IScreenCapture capture, TimeProvider? clock = null)
{
    private readonly IScreenCapture _capture = capture;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Interval between rounds: short enough to beat a human's patience, long enough that the rounds
    /// fit inside most scenes' repaint cadence.</summary>
    internal static readonly TimeSpan RoundDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The deadline a caller can request, as the sensor enforces it.</summary>
    internal static TimeSpan ClampTimeout(TimeSpan requested) =>
        requested <= TimeSpan.Zero ? TimeSpan.FromSeconds(5)
        : requested > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5)
        : requested;

    private static string Hash(CaptureResult frame)
    {
        // A hash, not the bytes: the comparison is equal/unequal, and keeping whole frames alive between rounds
        // is memory the sensor does not need to spend.
        var bytes = MD5.HashData(frame.Bgra);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Waits until two consecutive captures of the window hash identically, or the deadline passes. Returns how
    /// long it took, whether it settled, and the last frame's shape.
    /// </summary>
    public async Task<(bool Stable, double WaitedMs, int Width, int Height, int Rounds)> WaitStillAsync(
        nint handle, TimeSpan timeout, CancellationToken ct = default)
    {
        var budget = ClampTimeout(timeout);
        var startedAt = _clock.GetUtcNow();
        string? previous = null;
        var rounds = 0;
        var width = 0;
        var height = 0;

        while (true)
        {
            rounds++;
            var frame = await _capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false);
            if (!frame.Ok)
            {
                return (false, (_clock.GetUtcNow() - startedAt).TotalMilliseconds, 0, 0, rounds);
            }

            width = frame.Width;
            height = frame.Height;
            var hash = Hash(frame);

            if (hash == previous)
            {
                return (true, (_clock.GetUtcNow() - startedAt).TotalMilliseconds, width, height, rounds);
            }

            previous = hash;

            if (_clock.GetUtcNow() - startedAt + RoundDelay > budget)
            {
                return (false, (_clock.GetUtcNow() - startedAt).TotalMilliseconds, width, height, rounds);
            }

            await Task.Delay(RoundDelay, ct).ConfigureAwait(false);
        }
    }
}
