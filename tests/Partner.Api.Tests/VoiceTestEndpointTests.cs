using Partner.Api.Endpoints;

namespace Partner.Api.Tests;

/// <summary>
/// The settings page's "test" verdicts. Only the pure part is pinned here: the device and the engine
/// round-trip need real hardware, the rule they feed does not — and the rule is the part that used to lie.
/// </summary>
public sealed class VoiceTestEndpointTests
{
    /// <summary>
    /// Digital silence means the microphone delivered nothing. The engine being reachable must not turn that
    /// green, or an unplugged device, a quiet room and a threshold set too high all look identical.
    /// </summary>
    [Fact]
    public void A_recording_with_no_signal_is_reported_as_a_failure()
    {
        var result = VoiceTestEndpoints.JudgeAsr(new float[16_000], 0.03, "", "实听 2 s", 12);

        Assert.False(result.Ok);
        Assert.Contains("没听到声音", result.Detail, StringComparison.Ordinal);
        Assert.Contains("0.0000", result.Detail, StringComparison.Ordinal);
        Assert.Equal(12, result.LatencyMs);
    }

    /// <summary>1 LSB of dither is the other common shape of "the device is not really delivering audio".</summary>
    [Fact]
    public void A_recording_at_the_noise_floor_is_a_failure_too()
    {
        var audio = Enumerable.Repeat(1f / 32768f, 16_000).ToArray();

        var result = VoiceTestEndpoints.JudgeAsr(audio, 0.03, "", "实听 2 s", 0);

        Assert.False(result.Ok);
        Assert.Contains("没听到声音", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An engine that hallucinates text out of silence must not flip the verdict: the audio is the evidence,
    /// the transcript is the engine's opinion about it.
    /// </summary>
    [Fact]
    public void A_transcript_does_not_turn_silence_green()
    {
        var result = VoiceTestEndpoints.JudgeAsr(new float[16_000], 0.03, "谢谢观看", "实听 2 s", 30);

        Assert.False(result.Ok);
        Assert.Contains("谢谢观看", result.Detail, StringComparison.Ordinal);
        Assert.Equal("谢谢观看", result.Transcribed);
    }

    [Fact]
    public void A_recording_with_audio_and_a_transcript_passes()
    {
        var audio = Enumerable.Repeat(0.1f, 16_000).ToArray();

        var result = VoiceTestEndpoints.JudgeAsr(audio, 0.03, " 今天天气怎么样 ", "实听 2 s", 30);

        Assert.True(result.Ok);
        Assert.Contains("今天天气怎么样", result.Detail, StringComparison.Ordinal);
        Assert.Equal("今天天气怎么样", result.Transcribed);
    }

    /// <summary>
    /// Audio but no transcript stays a pass — the engine is reachable and heard something — yet the level is
    /// written into the detail, because "quiet room" and "engine cannot hear" are different problems.
    /// </summary>
    [Fact]
    public void A_recording_with_audio_but_no_transcript_reports_the_level()
    {
        var audio = Enumerable.Repeat(0.1f, 16_000).ToArray();

        var result = VoiceTestEndpoints.JudgeAsr(audio, 0.03, "   ", "实听 2 s", 30);

        Assert.True(result.Ok);
        Assert.Contains("没识别出内容", result.Detail, StringComparison.Ordinal);
        Assert.Contains("0.1000", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>A device that could not be opened leaves no audio at all: that is the same verdict.</summary>
    [Fact]
    public void The_silence_probe_used_when_the_device_is_unavailable_is_a_failure()
    {
        var result = VoiceTestEndpoints.JudgeAsr(new float[8_000], 0.03, "", "输入设备不可用（被占用），用 0.5 s 静音探测", 5);

        Assert.False(result.Ok);
        Assert.Contains("输入设备不可用", result.Detail, StringComparison.Ordinal);
    }
}
