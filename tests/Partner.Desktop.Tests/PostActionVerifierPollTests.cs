using Partner.Desktop.Capture;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>
/// The recheck loop: repaints are asynchronous, so a NoChange measured within milliseconds of the injection
/// can be a frame the application has not drawn yet, and reporting it as "nothing changed" is what produced
/// the retries that double-triggered toggles. These tests hold the loop's two ends: a change that arrives on
/// any later capture wins on the round it appeared, and a loop that runs out still reports exactly what was
/// measured — no change, once per round, on every round until now.
/// </summary>
public sealed class PostActionVerifierPollTests
{
    private const int Width = 64;
    private const int Height = 64;

    [Fact]
    public async Task A_repaint_that_arrives_after_the_first_capture_is_confirmed_on_the_later_round()
    {
        var capture = new SequenceCapture(unchanged: Frame(), changed: Frame(dirty: (0, 0, 8, 8)));
        var verifier = new PostActionVerifier(capture);

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, Width, Height), Before());

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Equal(2, capture.WindowCaptures);
        // The loop waited, at least one round, rather than answering immediately: a finding the first frame
        // could not support on its own.
        Assert.True(
            result.ElapsedMs >= PostActionVerifier.DefaultRecheckDelay.TotalMilliseconds,
            $"elapsed {result.ElapsedMs:0.0}ms < one recheck delay {PostActionVerifier.DefaultRecheckDelay.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task Change_that_arrives_on_the_last_round_is_still_found()
    {
        // Two unchanged rounds and then the repaint: the loop's shape, not the timing, is what decides.
        var capture = new SequenceCapture(
            unchanged: Frame(),
            changed: Frame(dirty: (0, 0, 8, 8)),
            unchangedCapturesBeforeChange: 2);
        var verifier = new PostActionVerifier(capture);

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, Width, Height), Before());

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Equal(3, capture.WindowCaptures);
    }

    [Fact]
    public async Task A_window_that_never_repaints_is_no_change_after_the_rounds_have_run_out()
    {
        var capture = new SequenceCapture(unchanged: Frame(), changed: null);
        var verifier = new PostActionVerifier(capture);

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, Width, Height), Before());

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Equal(0, result.IntersectedAreaRatio);
        // One round per recheck, plus the first: the "no" is the count of times it was actually measured.
        Assert.Equal(verifier.RecheckRounds + 1, capture.WindowCaptures);
    }

    [Fact]
    public async Task Zero_recheck_rounds_is_exactly_the_single_measurement_it_used_to_be()
    {
        // The poll loop is an addition on top of the reference behaviour, not a replacement of it: configured
        // off, the verifier measures once and answers once, which is the shape the older tests pin down.
        var capture = new SequenceCapture(unchanged: Frame(), changed: null);
        var verifier = new PostActionVerifier(capture, recheckRounds: 0);

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, Width, Height), Before());

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Equal(1, capture.WindowCaptures);
    }

    [Fact]
    public async Task The_region_path_polls_the_same_way()
    {
        // The cheap path is the common one for a coordinate click, and its asynchronous-repaint risk is the
        // same: the recheck has to re-capture the region, not fall back to a window render.
        var capture = new SequenceCapture(
            unchanged: Frame(),
            changed: Frame(dirty: (0, 0, 8, 8)),
            region: WindowOnScreen);
        var verifier = new PostActionVerifier(capture);

        var result = await verifier.VerifyScreenRegionAsync(
            1,
            new PixelRect(WindowOnScreen.X, WindowOnScreen.Y, 16, 16),
            Before(),
            screenCaptureOk: true);

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Equal(2, capture.RegionCaptures);
        Assert.Equal(0, capture.WindowCaptures);
    }

    private static PixelRect WindowOnScreen => new(100, 100, Width, Height);

    private static CaptureResult Before() => new()
    {
        Ok = true,
        Width = Width,
        Height = Height,
        Bgra = Frame(),
        Method = "test",
        ScreenRect = WindowOnScreen,
    };

    private static byte[] Frame(params (int X, int Y, int W, int H)[] dirty)
    {
        var buffer = new byte[Width * Height * 4];
        Array.Fill(buffer, (byte)0x20);

        foreach (var (x, y, w, h) in dirty)
        {
            for (var row = y; row < y + h; row++)
            {
                for (var col = x; col < x + w; col++)
                {
                    var index = ((row * Width) + col) * 4;
                    buffer[index] = 0xFF;
                    buffer[index + 1] = 0xFF;
                    buffer[index + 2] = 0xFF;
                }
            }
        }

        return buffer;
    }

    /// <summary>
    /// Hands out the unchanged frame for the first <paramref name="unchangedCapturesBeforeChange"/> captures,
    /// then the repaint frame — and keeps it there once the change has happened. A null repaint frame means
    /// no repaint ever arrives. Window captures and region captures each keep their own count, so a test can
    /// say which path a recheck stayed on.
    /// </summary>
    private sealed class SequenceCapture : IScreenCapture
    {
        private readonly object _gate = new();
        private readonly byte[] _unchanged;
        private readonly byte[]? _changed;
        private readonly PixelRect? _region;
        private readonly int _unchangedCapturesBeforeChange;

        /// <summary><paramref name="unchangedCapturesBeforeChange"/> captures come back unchanged, then the
        /// repaint lands and stays. Null <paramref name="changed"/> means it never arrives.</summary>
        private int _windowCaptures;
        private int _regionCaptures;

        public SequenceCapture(byte[] unchanged, byte[]? changed, PixelRect? region = null, int unchangedCapturesBeforeChange = 1)
        {
            _unchanged = unchanged;
            _changed = changed;
            _region = region;
            _unchangedCapturesBeforeChange = unchangedCapturesBeforeChange;
        }

        public int WindowCaptures
        {
            get { lock (_gate) { return _windowCaptures; } }
        }

        public int RegionCaptures
        {
            get { lock (_gate) { return _regionCaptures; } }
        }

        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _windowCaptures++;
                var repaint = _changed is not null && _windowCaptures > _unchangedCapturesBeforeChange;
                return Task.FromResult(StaticResult(repaint ? _changed! : _unchanged, "test"));
            }
        }

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int w, int h, CancellationToken ct = default)
        {
            int captures;
            lock (_gate)
            {
                _regionCaptures++;
                captures = _regionCaptures;
            }

            var repaint = _changed is not null && captures > _unchangedCapturesBeforeChange;
            var full = StaticResult(repaint ? _changed! : _unchanged, "test");
            var crop = PostActionVerifier.Crop(full, x - _region!.X, y - _region.Y, w, h);
            return Task.FromResult((crop ?? throw new InvalidOperationException("the region does not fit the frame"))
                with { Method = "test.region" });
        }

        private static CaptureResult StaticResult(byte[] frame, string method) => new()
        {
            Ok = true,
            Width = Width,
            Height = Height,
            Bgra = frame,
            Method = method,
        };
    }
}
