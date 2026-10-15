using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice.Tests;

/// <summary>
/// 唤醒门 VoiceInputHalf + IWakeChecker 的行为：未唤醒不提交，唤醒后一段话内正常工作，说完全闭闸回去。
/// </summary>
public sealed class WakeGateTests
{
    private const int SampleRate = 16_000;

    private sealed class ScriptedWake : IWakeChecker
    {
        public Func<float[], LocalWakeClient.WakeHit>? Respond { get; set; }

        public int Polls { get; private set; }

        /// <summary>每次探针帧的长度，用来证明前置音频真的拼上去了。</summary>
        public List<int> FrameLengths { get; } = new();

        public Task<LocalWakeClient.WakeHit> PollAsync(float[] frame, CancellationToken ct = default)
        {
            Polls++;
            FrameLengths.Add(frame.Length);
            return Task.FromResult(Respond?.Invoke(frame)
                ?? new LocalWakeClient.WakeHit(null, 0, 0));
        }
    }

    private static (VoiceInputHalf Half, SyntheticAudioSource Source, ScriptedWake Wake, List<VoiceUtterance> Utterances) Create(
        VoiceInputSetting? setting = null)
    {
        var source = new SyntheticAudioSource(SampleRate);
        var wake = new ScriptedWake();
        var utterances = new List<VoiceUtterance>();
        setting ??= new VoiceInputSetting
        {
            Enabled = true,
            SilenceThreshold = 0.03,
            UtteranceGapMs = 1200,
            WindowSeconds = 8,
        };
        var half = new VoiceInputHalf(
            setting with { Wake = wake },
            source,
            new NullAsr(),
            new PlaybackGate(),
            utterance =>
            {
                utterances.Add(utterance);
                return Task.CompletedTask;
            });

        half.Start();
        return (half, source, wake, utterances);
    }

    /// <summary>推进 n 秒的静音或语音帧（20ms 一步），between 每步 tick 一次。</summary>
    private static async Task AdvanceAsync(VoiceInputHalf half, SyntheticAudioSource source, double seconds, float amplitude)
    {
        const int frame = 320; // 20ms at 16kHz
        for (var i = 0; i < seconds * SampleRate / frame; i++)
        {
            source.Emit(amplitude, frame);
            await half.TickAsync();
        }
    }

    [Fact]
    public async Task Before_wake_speech_produces_no_utterance()
    {
        var (half, source, wake, utterances) = Create();

        // 2 秒持续说话。没有唤醒词命中。
        await AdvanceAsync(half, source, 2.0, 0.5f);

        Assert.Empty(utterances);
        Assert.False(half.Awake);
        Assert.True(wake.Polls > 0); // 麦克风在听，KWS 在被问
    }

    [Fact]
    public async Task Wake_word_opens_the_gate_and_speech_submits()
    {
        var (half, source, wake, utterances) = Create();

        wake.Respond = frame => new LocalWakeClient.WakeHit("小爱同学", 0, 0.9);
        await AdvanceAsync(half, source, 0.2, 0f); // 唤醒词本身过来（当噪声也罢）
        Assert.True(half.Awake);
        wake.Respond = null;

        // 唤醒后说一句话，然后沉默 1.4s 让 G2 闭合提交。
        await AdvanceAsync(half, source, 1.0, 0.5f);
        await AdvanceAsync(half, source, 1.4, 0f);

        Assert.Single(utterances);
        Assert.False(half.Awake); // 提交即一段话完，回闸等下次唤醒。
        Assert.True(half.Utterances.Single().Transcript.Length > 0);
    }

    [Fact]
    public async Task Wake_hit_plays_the_tone_exactly_once_per_transition()
    {
        var tones = 0;
        var source = new SyntheticAudioSource(SampleRate);
        var wake = new ScriptedWake();
        var half = new VoiceInputHalf(
            new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = 0.03,
                UtteranceGapMs = 1200,
                WindowSeconds = 8,
                Wake = wake,
                WakeTone = true,
            },
            source,
            new NullAsr(),
            new PlaybackGate(),
            _ => Task.CompletedTask,
            wakeTone: () => tones++);

        half.Start();
        wake.Respond = frame => new LocalWakeClient.WakeHit("军哥", 0, 0.9);
        await AdvanceAsync(half, source, 1.0, 0.1f);

        // 多次 tick 只在关→开的那一瞬达拨过一声；引擎持续命中时不再重复。
        Assert.Equal(1, tones);

        wake.Respond = null;
        await AdvanceAsync(half, source, 3.0, 0.5f);
        await AdvanceAsync(half, source, 1.5, 0f);
        Assert.False(half.Awake); // 会话闭了。

        wake.Respond = frame => new LocalWakeClient.WakeHit("军哥", 0, 0.9);
        await AdvanceAsync(half, source, 1.0, 0.1f);
        Assert.Equal(2, tones); // 第二次唤醒再拨一声——每次进入会话都有。
    }

    [Fact]
    public async Task Wake_poll_failure_does_not_kill_the_half()
    {
        var (half, source, _, utterances) = Create();

        // 模拟引擎失联：PollAsync 抛。
        await AdvanceAsync(half, source, 0.5, 0.5f);

        Assert.Empty(utterances);
        Assert.True(half.IsRunning); // 半区活着。
    }

    /// <summary>
    /// 探针帧必须比纯增量长：KWS 解码器要左边上下文才命中（实测前置 0.3s 打不出、1.0s 才命中），
    /// 裸增量永远不会响。前置窗口要等音频写满 1s 才成形，所以只对那之后的探针断言。
    /// </summary>
    [Fact]
    public async Task Wake_probe_carries_lead_context_not_bare_increment()
    {
        var (half, source, wake, _) = Create();

        // 4s 语音：远超 1s 前置窗口，足够发生多次「前置已满」的探针。
        await AdvanceAsync(half, source, 4.0, 0.5f);

        Assert.True(wake.Polls > 0);
        Assert.True(wake.FrameLengths.Count >= 20, "not enough probes to judge the lead window");

        // 最小增量 100ms + 满前置 1s = 1.1s；少一个样本都说明前置掉了。
        var settled = wake.FrameLengths.Skip(10).ToList();
        Assert.All(settled, length => Assert.True(
            length >= (int)(SampleRate * 1.1),
            $"probe frame {length} samples lacks the 1s lead context"));
    }

    /// <summary>前置窗口之外的音频不占探针帧：帧有上限，否则一次请求会灌进整段历史。</summary>
    [Fact]
    public async Task Wake_probe_frame_stays_bounded()
    {
        var (half, source, wake, _) = Create();

        await AdvanceAsync(half, source, 4.0, 0.5f);

        Assert.True(wake.Polls > 0);
        // 增量上限 2s + 前置 1s，再加一个 tick 的余量。
        Assert.All(wake.FrameLengths, length => Assert.True(
            length <= SampleRate * 3.2,
            $"probe frame {length} samples exceeds the 1s lead + 2s increment budget"));
    }

    [Fact]
    public async Task No_wake_client_means_unchanged_batch_behavior()
    {
        var source = new SyntheticAudioSource(SampleRate);
        var utterances = new List<VoiceUtterance>();
        var half = new VoiceInputHalf(
            new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = 0.03,
                UtteranceGapMs = 1200,
                WindowSeconds = 8,
                Wake = null,
            },
            source,
            new NullAsr(),
            new PlaybackGate(),
            utterance =>
            {
                utterances.Add(utterance);
                return Task.CompletedTask;
            });

        half.Start();
        await AdvanceAsync(half, source, 1.0, 0.5f);
        await AdvanceAsync(half, source, 1.4, 0f);

        Assert.Single(utterances); // 没有唤醒门，一段话直接进。
    }

    /// <summary>固定返回一段文本的假 ASR，让每段提交都必产 utterance。</summary>
    private sealed class NullAsr : IAsrClient
    {
        public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default) =>
            Task.FromResult("测试转写");

        public IAsyncEnumerable<string> TranscribeStreamAsync(
            float[] pcm,
            CancellationToken ct = default)
        {
            return SingleAsync();

            async IAsyncEnumerable<string> SingleAsync()
            {
                yield return "测试转写";
                await Task.CompletedTask;
            }
        }

        public void Dispose()
        {
        }
    }
}
