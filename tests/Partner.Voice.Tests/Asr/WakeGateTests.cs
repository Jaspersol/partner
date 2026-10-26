using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice.Tests;

/// <summary>
/// Wake gate behavior of VoiceInputHalf with IWakeChecker: nothing submits before a wake hit, speech after a
/// hit submits, and the gate closes once the utterance ends.
/// </summary>
public sealed class WakeGateTests
{
    private const int SampleRate = 16_000;

    private sealed class ScriptedWake : IWakeChecker
    {
        public Func<float[], LocalWakeClient.WakeHit>? Respond { get; set; }

        public int Polls { get; private set; }

        /// <summary>Length of each probe frame, showing whether lead audio was prepended.</summary>
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

            // Deliberately short: these tests cover silence closing the session, not the grace period.
            // WakeTimingTests covers the real default (5000) and how long after a wake speech still counts.
            WakeTimeoutMs = 1200,
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

    /// <summary>Advances n seconds of silence or speech in 20 ms steps, ticking once per step.</summary>
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

        // Two seconds of speech, with no wake hit.
        await AdvanceAsync(half, source, 2.0, 0.5f);

        Assert.Empty(utterances);
        Assert.False(half.Awake);
        Assert.True(wake.Polls > 0); // The microphone listens and KWS is polled.
    }

    [Fact]
    public async Task Wake_word_opens_the_gate_and_speech_submits()
    {
        var (half, source, wake, utterances) = Create();

        wake.Respond = frame => new LocalWakeClient.WakeHit("小爱同学", 0, 0.9);
        await AdvanceAsync(half, source, 0.2, 0f); // A wake hit arrives, treated as noise by the gate.
        Assert.True(half.Awake);
        wake.Respond = null;

        // Speak after the wake, then stay silent for 1.4 s so the gap closes and submits.
        await AdvanceAsync(half, source, 1.0, 0.5f);
        await AdvanceAsync(half, source, 1.4, 0f);

        Assert.Single(utterances);
        Assert.False(half.Awake); // A submit ends the utterance and the gate waits for the next wake.
        Assert.True(half.Utterances.Single().Transcript.Length > 0);
        Assert.Equal("小爱同学", half.Utterances.Single().WakeWord);
    }

    [Fact]
    public async Task Approval_listening_bypasses_the_wake_word_until_it_is_stopped()
    {
        var (half, source, wake, utterances) = Create();

        Assert.True(half.WakeEnabled);
        Assert.False(half.ApprovalListening);

        half.SetApprovalListening(true);
        Assert.True(half.ApprovalListening);

        // Speech without a wake hit: pending approval is what lets it through.
        await AdvanceAsync(half, source, 1.0, 0.5f);
        await AdvanceAsync(half, source, 1.4, 0f);

        Assert.Single(utterances);
        Assert.True(utterances.Single().ApprovalListening);
        Assert.Equal(0, wake.Polls);

        // The request is settled: the ordinary wake rule returns.
        half.SetApprovalListening(false);
        await AdvanceAsync(half, source, 1.0, 0f);

        Assert.False(half.ApprovalListening);
        Assert.True(wake.Polls > 0);
    }

    /// <summary>A wake hit followed by silence must close the session itself.</summary>
    [Fact]
    public async Task A_wake_hit_without_following_speech_closes_the_session()
    {
        var (half, source, wake, utterances) = Create();

        wake.Respond = frame => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await AdvanceAsync(half, source, 0.2, 0f);
        Assert.True(half.Awake);

        // Silence after the wake word must close the session through the gap, without waiting for a transcript.
        wake.Respond = null;
        var pollsWhileAwake = wake.Polls;
        await AdvanceAsync(half, source, 2.5, 0f);

        Assert.False(half.Awake);
        Assert.Empty(utterances); // Pure silence is not worth an ASR request.
        Assert.True(wake.Polls > pollsWhileAwake, "探针必须在会话关掉之后重新跑起来");

        // A second wake must re-enter the session (the tone path is covered by Wake_hit_plays_the_tone).
        wake.Respond = frame => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await AdvanceAsync(half, source, 0.2, 0f);
        Assert.True(half.Awake);
    }

    /// <summary>
    /// A hit starts a new engine stream, so the first probe after the session ends must resend the lead;
    /// otherwise the new stream has no left context and a second wake silently fails.
    /// </summary>
    [Fact]
    public async Task Wake_probe_re_primes_the_stream_after_a_hit()
    {
        var (half, source, wake, _) = Create();

        wake.Respond = frame => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await AdvanceAsync(half, source, 0.2, 0f);
        Assert.True(half.Awake);

        var probesAtHit = wake.FrameLengths.Count;
        wake.Respond = null;
        await AdvanceAsync(half, source, 3.0, 0f);
        Assert.False(half.Awake);

        Assert.True(wake.FrameLengths.Count > probesAtHit, "探针没有在会话结束后恢复");
        var resumed = wake.FrameLengths[probesAtHit];

        // The increment is capped at 2 s, so a longer frame can only come from a resent lead.
        Assert.True(
            resumed > SampleRate * 2,
            $"first probe after the session is {resumed} samples — the lead was not re-sent");
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

        // Ticks across the closed-to-open transition play the tone once; a continuing hit does not repeat it.
        Assert.Equal(1, tones);

        wake.Respond = null;
        await AdvanceAsync(half, source, 3.0, 0.5f);
        await AdvanceAsync(half, source, 1.5, 0f);
        Assert.False(half.Awake); // The session is closed.

        wake.Respond = frame => new LocalWakeClient.WakeHit("军哥", 0, 0.9);
        await AdvanceAsync(half, source, 1.0, 0.1f);
        Assert.Equal(2, tones); // A second wake plays it again: every entry into a session has one.
    }

    [Fact]
    public async Task Wake_poll_failure_does_not_kill_the_half()
    {
        var (half, source, _, utterances) = Create();

        // Simulate a lost engine: PollAsync throws.
        await AdvanceAsync(half, source, 0.5, 0.5f);

        Assert.Empty(utterances);
        Assert.True(half.IsRunning); // The half stays alive.
    }

    /// <summary>
    /// The lead is sent once: a new stream needs left context to hit, but sending it every frame makes the
    /// stream hear each phrase twice, which can fake a wake word.
    /// </summary>
    [Fact]
    public async Task Wake_probe_primes_the_stream_once_then_sends_bare_increments()
    {
        var (half, source, wake, _) = Create();

        // Four seconds of speech: well past the 1 s lead window, enough for several probes.
        await AdvanceAsync(half, source, 4.0, 0.5f);

        Assert.True(wake.Polls > 0);
        Assert.True(wake.FrameLengths.Count >= 20, "not enough probes to judge the lead window");

        // First frame prepends whatever lead is available; at this point the ring holds 100 ms, so 3200 samples.
        Assert.True(
            wake.FrameLengths[0] > 320,
            $"first probe {wake.FrameLengths[0]} samples carries no lead at all");

        // After that, bare increments: 100 ms, or at least 1.1 s if the lead were resent.
        Assert.All(wake.FrameLengths.Skip(1), length => Assert.True(
            length < (int)(SampleRate * 1.1),
            $"probe frame {length} samples re-sent the lead context"));
    }

    /// <summary>Audio outside the lead window does not occupy probe frames, which are bounded.</summary>
    [Fact]
    public async Task Wake_probe_frame_stays_bounded()
    {
        var (half, source, wake, _) = Create();

        await AdvanceAsync(half, source, 4.0, 0.5f);

        Assert.True(wake.Polls > 0);
        // A 2 s increment cap, a 1 s lead, and one tick of margin.
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

        Assert.Single(utterances); // Without a wake gate, the utterance goes straight through.
    }

    /// <summary>A fake ASR that always returns text, so every submit produces an utterance.</summary>
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
