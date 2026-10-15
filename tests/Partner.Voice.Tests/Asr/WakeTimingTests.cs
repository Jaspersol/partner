using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice.Tests;

/// <summary>
/// 唤醒会话的时间边界：提示音到用户开口之间那段沉默是「在想说什么」，不是「说完了」。
/// <para>
/// 曾经这段沉默按判句间隔（<see cref="VoiceInputSetting.UtteranceGapMs"/>）计，于是唤醒后停 ~1.1 s
/// 再说话，会话已经关掉——之后说的全部只喂给唤醒探针，永远不进 ASR。用户看到的就是「唤醒之后说的话
/// 没变成输入」。宽限期（<see cref="VoiceInputSetting.WakeTimeoutMs"/>）把这两件事分开。
/// </para>
/// </summary>
public sealed class WakeTimingTests
{
    private const int SampleRate = 16_000;

    private sealed class ScriptedWake : IWakeChecker
    {
        public Func<float[], LocalWakeClient.WakeHit>? Respond { get; set; }

        public Task<LocalWakeClient.WakeHit> PollAsync(float[] frame, CancellationToken ct = default) =>
            Task.FromResult(Respond?.Invoke(frame) ?? new LocalWakeClient.WakeHit(null, 0, 0));
    }

    /// <summary>固定返回一段文本，让每次提交都产 utterance——「有没有进 ASR」由调用次数回答。</summary>
    private sealed class CountingAsr : IAsrClient
    {
        public int Calls { get; private set; }

        public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult("打开书房的灯");
        }

        public IAsyncEnumerable<string> TranscribeStreamAsync(float[] pcm, CancellationToken ct = default)
        {
            return SingleAsync();

            async IAsyncEnumerable<string> SingleAsync()
            {
                yield return "打开书房的灯";
                await Task.CompletedTask;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed record Rig(
        VoiceInputHalf Half,
        SyntheticAudioSource Source,
        ScriptedWake Wake,
        CountingAsr Asr,
        List<VoiceUtterance> Utterances)
    {
        public async Task AdvanceAsync(double seconds, float amplitude)
        {
            const int frame = 320; // 20 ms at 16 kHz
            for (var i = 0; i < seconds * SampleRate / frame; i++)
            {
                Source.Emit(amplitude, frame);
                await Half.TickAsync();
            }
        }

        /// <summary>唤醒一次：命中那一 tick 之后关掉探针，模拟引擎命中后换流。</summary>
        public async Task WakeAsync()
        {
            Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
            await AdvanceAsync(0.2, 0f);
            Wake.Respond = null;
        }
    }

    private static Rig Create(double wakeTimeoutMs = 5000, double utteranceGapMs = 1200)
    {
        var source = new SyntheticAudioSource(SampleRate);
        var wake = new ScriptedWake();
        var asr = new CountingAsr();
        var utterances = new List<VoiceUtterance>();
        var half = new VoiceInputHalf(
            new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = 0.03,
                UtteranceGapMs = utteranceGapMs,
                WakeTimeoutMs = wakeTimeoutMs,
                WindowSeconds = 8,
                Wake = wake,
            },
            source,
            asr,
            new PlaybackGate(),
            utterance =>
            {
                utterances.Add(utterance);
                return Task.CompletedTask;
            });

        half.Start();
        return new Rig(half, source, wake, asr, utterances);
    }

    /// <summary>
    /// 唤醒后隔一段时间才开口：只要没超过宽限期，说的话必须进 ASR。
    /// 这条在引入宽限期之前必红（1.2 s 与 2.0 s 两档实测 utterances=0 / asrCalls=0）。
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.8)]
    [InlineData(1.2)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task Speech_after_a_thinking_pause_reaches_the_asr(double pauseSeconds)
    {
        var rig = Create();
        await rig.WakeAsync();
        await rig.AdvanceAsync(pauseSeconds, 0f);

        await rig.AdvanceAsync(1.0, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
        Assert.Equal("打开书房的灯", rig.Utterances[0].Transcript);
        Assert.False(rig.Half.Awake);
    }

    /// <summary>宽限期之外仍然要关会话，否则误唤醒会一直敞着闸门收环境音。</summary>
    [Fact]
    public async Task A_session_where_nobody_speaks_still_closes_at_the_grace_period()
    {
        var rig = Create(wakeTimeoutMs: 5000);
        await rig.WakeAsync();

        // 宽限期内的沉默：会话还开着。
        await rig.AdvanceAsync(3.0, 0f);
        Assert.True(rig.Half.Awake);

        // 越过宽限期（5 s - 100 ms 补偿）：关会话，不提交（纯静音不值得一次 ASR 请求）。
        await rig.AdvanceAsync(2.5, 0f);
        Assert.False(rig.Half.Awake);
        Assert.Empty(rig.Utterances);
        Assert.Equal(0, rig.Asr.Calls);
    }

    /// <summary>
    /// 一开口宽限期就结束：此后按判句间隔收尾，不能拿 5 s 去等一句话的结尾。
    /// 说了 1 s 之后停 1.4 s（&gt; UtteranceGapMs）必须已经切好，而不是还在等宽限期。
    /// </summary>
    [Fact]
    public async Task Speaking_ends_the_grace_period_immediately()
    {
        var rig = Create(wakeTimeoutMs: 30_000, utteranceGapMs: 1200);
        await rig.WakeAsync();

        await rig.AdvanceAsync(1.0, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
        Assert.False(rig.Half.Awake);
    }

    /// <summary>唤醒词和命令一口气说完：命中发生在说话中途，命令必须完整进 ASR。</summary>
    [Fact]
    public async Task A_command_in_the_same_breath_as_the_wake_word()
    {
        var rig = Create();

        // 连续说话，唤醒在说话中途命中（引擎有 ~320 ms 延迟，模拟成第 0.5 s 命中）。
        await rig.AdvanceAsync(0.5, 0.5f);
        rig.Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await rig.AdvanceAsync(0.3, 0.5f);
        rig.Wake.Respond = null;
        await rig.AdvanceAsync(1.5, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
    }

    /// <summary>
    /// 唤醒词和命令一口气说完、中间不停：KWS 有 ~320 ms 检测延迟，命中那一刻命令往往已经说完，
    /// 命中点之前的音频必须回溯进块。曾经游标定在命中时刻，于是整块只剩静音 → SpeechFraction 不达标
    /// → 提前 return → 命令永远进不了 ASR（用户看到「唤醒后说的话没进对话」）。
    /// </summary>
    [Fact]
    public async Task A_command_finished_before_the_hit_still_reaches_the_asr()
    {
        var rig = Create();

        // 唤醒词 + 命令连读，1.2 s 全说完。
        await rig.AdvanceAsync(1.2, 0.5f);

        // 引擎 ~320 ms 检测延迟：命中在语音结束后才回来。
        rig.Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await rig.AdvanceAsync(0.12, 0f);
        rig.Wake.Respond = null;

        // 静默闭合提交。
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
        Assert.Equal("打开书房的灯", rig.Utterances[0].Transcript);
    }

    /// <summary>
    /// 唤醒词是真声、命中带 ~320 ms 延迟（chunk-16）：回溯窗里是「唤醒词尾巴 + 静默」，不是成片人声，
    /// 宽限期必须仍生效。否则「喊完词停着想一下再说」会退化成 1.2 s 关会话，b7396f8 白修。
    /// </summary>
    [Fact]
    public async Task A_thinking_pause_after_a_spoken_wake_word_still_waits()
    {
        var rig = Create();

        // 唤醒词是真声（0.6 s），说完后引擎 ~320 ms 检测延迟才报命中。
        await rig.AdvanceAsync(0.6, 0.5f);
        await rig.AdvanceAsync(0.32, 0f);
        rig.Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await rig.AdvanceAsync(0.12, 0f);
        rig.Wake.Respond = null;

        // 停 2 s 想一下再开口：宽限期必须还开着。
        await rig.AdvanceAsync(2.0, 0f);
        await rig.AdvanceAsync(1.0, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
        Assert.Equal("打开书房的灯", rig.Utterances[0].Transcript);
    }

    /// <summary>没有唤醒门时宽限期不参与：老行为（纯语音输入）一字不变。</summary>
    [Fact]
    public async Task Without_a_wake_gate_the_timeout_is_not_used()
    {
        var source = new SyntheticAudioSource(SampleRate);
        var asr = new CountingAsr();
        var utterances = new List<VoiceUtterance>();
        var half = new VoiceInputHalf(
            new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = 0.03,
                UtteranceGapMs = 1200,
                WakeTimeoutMs = 30_000,
                WindowSeconds = 8,
                Wake = null,
            },
            source,
            asr,
            new PlaybackGate(),
            utterance =>
            {
                utterances.Add(utterance);
                return Task.CompletedTask;
            });

        half.Start();
        for (var i = 0; i < 1.0 * SampleRate / 320; i++)
        {
            source.Emit(0.5f, 320);
            await half.TickAsync();
        }

        for (var i = 0; i < 1.4 * SampleRate / 320; i++)
        {
            source.Emit(0f, 320);
            await half.TickAsync();
        }

        Assert.Single(utterances);
    }
}
