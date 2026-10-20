using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice.Tests;

/// <summary>
/// The input level and the noise-floor probe. They are the only two things that tell a dead microphone, a
/// quiet room and speech below the gate's threshold apart on the settings page, so both have to follow the
/// audio — and the floor must not be measured while somebody is talking into it.
/// </summary>
public sealed class InputLevelTests
{
    private const int SampleRate = 16_000;

    private static (VoiceInputHalf Half, SyntheticAudioSource Source) Create(double silenceThreshold = 0.03)
    {
        var source = new SyntheticAudioSource(SampleRate);
        var half = new VoiceInputHalf(
            new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = silenceThreshold,
                UtteranceGapMs = 1200,
                WindowSeconds = 8,
            },
            source,
            new SilentAsr(),
            new PlaybackGate(),
            _ => Task.CompletedTask);

        half.Start();
        return (half, source);
    }

    /// <summary>Advances n seconds of silence or speech in 20 ms steps, ticking once per step.</summary>
    private static async Task AdvanceAsync(
        VoiceInputHalf half,
        SyntheticAudioSource source,
        double seconds,
        float amplitude)
    {
        const int frame = 320; // 20ms at 16kHz
        for (var i = 0; i < seconds * SampleRate / frame; i++)
        {
            source.Emit(amplitude, frame);
            await half.TickAsync();
        }
    }

    [Fact]
    public async Task The_reported_level_follows_the_microphone()
    {
        var (half, source) = Create();

        await AdvanceAsync(half, source, 1.0, 0f);
        Assert.True(half.RecentRms < 0.03, $"silence read as {half.RecentRms:0.0000}");

        await AdvanceAsync(half, source, 1.0, 0.5f);
        Assert.True(half.RecentRms > 0.03, $"speech read as {half.RecentRms:0.0000}");
    }

    /// <summary>
    /// Speech from the very first moment must not become the noise floor: the half would then report a
    /// threshold misconfiguration on every launch that starts with a spoken word — a warning that cries
    /// wolf is worse than no warning.
    /// </summary>
    [Fact]
    public async Task The_noise_floor_is_not_measured_while_someone_is_speaking()
    {
        var (half, source) = Create();

        await AdvanceAsync(half, source, 1.0, 0.5f);
        Assert.False(half.ThresholdMisconfigured);

        await AdvanceAsync(half, source, 1.0, 0f);

        Assert.False(half.ThresholdMisconfigured);
        Assert.True(half.AmbientRms < 0.03, $"ambient={half.AmbientRms:0.0000}");
    }

    /// <summary>
    /// A threshold nothing can fall below (0) makes silence undetectable, and that is exactly what must be
    /// reported. Waiting for a quiet window cannot settle it, so the deadline reports on the evidence it has.
    /// </summary>
    [Fact]
    public async Task A_threshold_nothing_can_fall_below_is_reported_once_the_probe_gives_up()
    {
        var (half, source) = Create(silenceThreshold: 0.0);

        await AdvanceAsync(half, source, 6.0, 0.05f);

        Assert.True(half.ThresholdMisconfigured);
        Assert.True(half.AmbientRms >= 0.0, $"ambient={half.AmbientRms:0.0000}");
    }

    /// <summary>A room that is never quiet enough is the same verdict, and it is not delayed forever.</summary>
    [Fact]
    public async Task A_room_that_never_goes_quiet_is_reported_too()
    {
        var (half, source) = Create(silenceThreshold: 0.03);

        await AdvanceAsync(half, source, 6.0, 0.05f);

        Assert.True(half.ThresholdMisconfigured);
        Assert.True(half.AmbientRms >= 0.03, $"ambient={half.AmbientRms:0.0000}");
    }

    /// <summary>An ASR that returns no text; these tests cover levels and noise floor, not transcription.</summary>
    private sealed class SilentAsr : IAsrClient
    {
        public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public IAsyncEnumerable<string> TranscribeStreamAsync(
            float[] pcm,
            CancellationToken ct = default)
        {
            return EmptyAsync();
        }

        private static async IAsyncEnumerable<string> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }
}
