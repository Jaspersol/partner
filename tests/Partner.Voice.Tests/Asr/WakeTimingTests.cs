using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice.Tests;

/// <summary>
/// Timing boundaries of a wake session: silence between the tone and the user speaking means "thinking",
/// not "finished". The grace period (<see cref="VoiceInputSetting.WakeTimeoutMs"/>) keeps that silence
/// separate from the utterance gap (<see cref="VoiceInputSetting.UtteranceGapMs"/>).
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

    /// <summary>Always returns text, so every submit produces an utterance; call count answers whether ASR ran.</summary>
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

    /// <summary>How often the tone callback ran; zero means the host never played one and echo assertions are moot.</summary>
    private sealed class ToneCounter
    {
        public int Plays;
    }

    private sealed record Rig(
        VoiceInputHalf Half,
        SyntheticAudioSource Source,
        ScriptedWake Wake,
        CountingAsr Asr,
        List<VoiceUtterance> Utterances,
        ToneCounter Tone)
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

        /// <summary>Wakes once, then stops responding to simulate a new stream after a hit.</summary>
        public async Task WakeAsync()
        {
            Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
            await AdvanceAsync(0.2, 0f);
            Wake.Respond = null;
        }

        /// <summary>
        /// The 0.16 s tone leaving the speaker and returning through the microphone: wait out the sink latency,
        /// then feed audio at 0.1 amplitude, well above the 0.03 threshold.
        /// </summary>
        public async Task WakeToneEchoAsync(double latencySeconds = 0.15)
        {
            await AdvanceAsync(latencySeconds, 0f);
            await AdvanceAsync(0.16, 0.1f);
        }
    }

    private static Rig Create(double wakeTimeoutMs = 5000, double utteranceGapMs = 1200, bool wakeTone = false)
    {
        var source = new SyntheticAudioSource(SampleRate);
        var wake = new ScriptedWake();
        var asr = new CountingAsr();
        var utterances = new List<VoiceUtterance>();
        var tone = new ToneCounter();
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
            },
            wakeTone: wakeTone ? () => tone.Plays++ : null);

        half.Start();
        return new Rig(half, source, wake, asr, utterances, tone);
    }

    /// <summary>Speech after a pause still reaches ASR as long as the grace period has not expired.</summary>
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

    /// <summary>The session closes at the grace period, or a false wake would keep collecting ambient audio.</summary>
    [Fact]
    public async Task A_session_where_nobody_speaks_still_closes_at_the_grace_period()
    {
        var rig = Create(wakeTimeoutMs: 5000);
        await rig.WakeAsync();

        // Silence inside the grace period keeps the session open.
        await rig.AdvanceAsync(3.0, 0f);
        Assert.True(rig.Half.Awake);

        // Past the grace period (5 s minus 100 ms of compensation) the session closes and submits nothing.
        await rig.AdvanceAsync(2.5, 0f);
        Assert.False(rig.Half.Awake);
        Assert.Empty(rig.Utterances);
        Assert.Equal(0, rig.Asr.Calls);
    }

    /// <summary>
    /// Speech ends the grace period immediately; the utterance then closes on the gap instead of waiting
    /// out the full wake timeout.
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

    /// <summary>A wake word and command in one breath: the hit lands mid-speech and the command must still reach ASR.</summary>
    [Fact]
    public async Task A_command_in_the_same_breath_as_the_wake_word()
    {
        var rig = Create();

        // Continuous speech, with the hit arriving mid-utterance at 0.5 s (about 320 ms of engine latency).
        await rig.AdvanceAsync(0.5, 0.5f);
        rig.Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await rig.AdvanceAsync(0.3, 0.5f);
        rig.Wake.Respond = null;
        await rig.AdvanceAsync(1.5, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
    }

    /// <summary>
    /// With about 320 ms of KWS latency the command may be finished before the hit; audio from before the hit
    /// must be included in the block, or it holds only silence and fails the speech-fraction gate.
    /// </summary>
    [Fact]
    public async Task A_command_finished_before_the_hit_still_reaches_the_asr()
    {
        var rig = Create();

        // Wake word plus command, all spoken within 1.2 s.
        await rig.AdvanceAsync(1.2, 0.5f);

        // About 320 ms of engine latency: the hit returns after speech has ended.
        rig.Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await rig.AdvanceAsync(0.12, 0f);
        rig.Wake.Respond = null;

        // Silence closes and submits the utterance.
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
        Assert.Equal("打开书房的灯", rig.Utterances[0].Transcript);
    }

    /// <summary>
    /// A spoken wake word, with the hit about 320 ms late: the look-back window holds the end of the word and
    /// some silence, not continuous speech, so the grace period must still apply.
    /// </summary>
    [Fact]
    public async Task A_thinking_pause_after_a_spoken_wake_word_still_waits()
    {
        var rig = Create();

        // The wake word is real speech (0.6 s); the engine reports the hit about 320 ms later.
        await rig.AdvanceAsync(0.6, 0.5f);
        await rig.AdvanceAsync(0.32, 0f);
        rig.Wake.Respond = _ => new LocalWakeClient.WakeHit("小克小克", 0, 0.9);
        await rig.AdvanceAsync(0.12, 0f);
        rig.Wake.Respond = null;

        // A two-second thinking pause: the grace period must still be open.
        await rig.AdvanceAsync(2.0, 0f);
        await rig.AdvanceAsync(1.0, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
        Assert.Equal("打开书房的灯", rig.Utterances[0].Transcript);
    }

    /// <summary>
    /// The wake tone echoing back through the microphone must not count as the user speaking; otherwise it
    /// expires the grace period and a command spoken after a pause never reaches ASR.
    /// </summary>
    [Fact]
    public async Task A_wake_tone_echo_does_not_end_the_grace_period()
    {
        var rig = Create(wakeTone: true);
        await rig.WakeAsync();
        await rig.WakeToneEchoAsync();

        // The user thinks before speaking: the grace period must still be open.
        await rig.AdvanceAsync(1.5, 0f);
        Assert.True(rig.Half.Awake);

        await rig.AdvanceAsync(1.0, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Equal(1, rig.Tone.Plays);
        Assert.Equal(1, rig.Asr.Calls);
        Assert.Single(rig.Utterances);
        Assert.Equal("打开书房的灯", rig.Utterances[0].Transcript);
        Assert.False(rig.Half.Awake);
    }

    /// <summary>
    /// Speech overlapping the wake tone: the suppression window only gates speech detection, not audio, so a
    /// command that continues past the window still reaches ASR.
    /// </summary>
    [Fact]
    public async Task A_command_that_starts_during_the_wake_tone_is_not_lost()
    {
        var rig = Create(wakeTone: true);
        await rig.WakeAsync();
        await rig.WakeToneEchoAsync();

        // The user speaks while the tone plays and keeps speaking past the suppression window.
        await rig.AdvanceAsync(1.2, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Equal(1, rig.Tone.Plays);
        Assert.Equal(1, rig.Asr.Calls);
        Assert.Single(rig.Utterances);
    }

    /// <summary>
    /// A short blip inside the grace period (cough, keyboard, bump) is not speech: a single 50 ms energy
    /// window above the threshold must not expire the grace period.
    /// </summary>
    [Fact]
    public async Task A_short_noise_blip_inside_the_grace_period_is_not_speech()
    {
        var rig = Create();
        await rig.WakeAsync();

        await rig.AdvanceAsync(1.0, 0f);
        await rig.AdvanceAsync(0.06, 0.5f); // A blip lasting three ticks.
        await rig.AdvanceAsync(1.5, 0f);

        Assert.True(rig.Half.Awake);

        await rig.AdvanceAsync(1.0, 0.5f);
        await rig.AdvanceAsync(1.4, 0f);

        Assert.Single(rig.Utterances);
    }

    /// <summary>
    /// Real Chinese speech is syllabic: each syllable is above the threshold for 60-190 ms, with 100-140 ms
    /// gaps below it. Requiring 200 ms of continuous speech would never expire the grace period, so the block
    /// would stretch to 5 s after the last syllable and <c>SpeechFraction</c> would dilute the command to noise.
    /// </summary>
    [Fact]
    public async Task Syllabic_speech_after_a_thinking_pause_reaches_the_asr()
    {
        var rig = Create(wakeTone: true);
        await rig.WakeAsync();
        await rig.WakeToneEchoAsync();
        await rig.AdvanceAsync(1.5, 0f);

        // Eight syllables: 100 ms voiced plus a 120 ms gap, each about 140 ms above the threshold per 50 ms window.
        for (var i = 0; i < 8; i++)
        {
            await rig.AdvanceAsync(0.10, 0.5f);
            await rig.AdvanceAsync(0.12, 0f);
        }

        await rig.AdvanceAsync(1.4, 0f);

        Assert.Equal(1, rig.Asr.Calls);
        Assert.Single(rig.Utterances);
        Assert.False(rig.Half.Awake);

        // Closes on the utterance gap, not the grace period: the block starts at the command (with pre-roll),
        // excluding the 1.5 s of silence after the wake.
        var utterance = rig.Utterances[0];
        Assert.InRange(utterance.SegEndMs - utterance.SegStartMs, 2500, 4000);
    }

    /// <summary>Without a wake gate the timeout does not apply; plain voice input behaves as before.</summary>
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
