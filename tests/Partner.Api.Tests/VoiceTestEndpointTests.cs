using Partner.Api.Endpoints;

namespace Partner.Api.Tests;

/// <summary>
/// The settings page's "test" verdicts. Only the pure part is pinned here: the device and the engine
/// round-trip need real hardware; the rule they feed does not, and that rule decides the verdict.
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

    /// <summary>
    /// A signal below the threshold is not "no device signal"; it means the level or threshold is misconfigured,
    /// even when the engine did transcribe. The message must say so, or "the engine transcribed anyway" sits
    /// next to "no device signal" and contradicts itself.
    /// </summary>
    [Fact]
    public void Quiet_speech_with_a_transcript_is_too_quiet_not_no_signal()
    {
        var audio = new float[16_000];
        for (var i = 0; i < 4_800; i++)
        {
            audio[i] = 0.01f;
        }

        var result = VoiceTestEndpoints.JudgeAsr(audio, 0.03, "你是谁", "实听 2 s", 39);

        Assert.False(result.Ok);
        Assert.Contains("声音太小", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("设备无信号", result.Detail, StringComparison.Ordinal);
        Assert.Contains("你是谁", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sentence occupies only part of the 2 s clip, so whole-clip RMS falls below the threshold while the
    /// loudest frame reaches it. The check must use the peak, or a normal short command shows as failing.
    /// </summary>
    [Fact]
    public void A_brief_loud_burst_is_not_diluted_by_silence()
    {
        var audio = new float[16_000];
        for (var i = 0; i < 1_600; i++)
        {
            audio[i] = 0.08f;
        }

        var result = VoiceTestEndpoints.JudgeAsr(audio, 0.03, "好", "实听 2 s", 5);

        Assert.True(result.Ok);
        Assert.Contains("好", result.Detail, StringComparison.Ordinal);
    }
}
