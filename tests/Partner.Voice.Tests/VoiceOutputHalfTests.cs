using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Tts;

namespace Partner.Voice.Tests;

/// <summary>
/// Playback order and synthesize-before-speaking: every sentence's audio must reach the sink before Release,
/// including on the second and later turns that reuse the same sink.
/// </summary>
public sealed class VoiceOutputHalfTests
{
    private static VoiceOutputHalf Half(IAudioSink sink, ITtsClient tts, out PlaybackGate gate)
    {
        gate = new PlaybackGate();
        return new VoiceOutputHalf(
            new VoiceOutputSetting { Enabled = true, MuteDuringTts = false, DrainMs = 0 },
            sink,
            tts,
            gate);
    }

    [Fact]
    public async Task Every_sentence_is_synthesised_before_any_of_them_plays()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = Half(sink, tts, out _);

        output.OnText("第一句话。第二句话。第三句话。", sessionEventSeq: 1);
        await output.FlushTurnAsync();

        Assert.Equal(new[] { "第一句话。", "第二句话。", "第三句话。" }, tts.Requests);
        Assert.Equal(3, sink.EnqueuedSegments);

        // Nothing leaked out: every segment arrived while held, and Release comes last.
        Assert.Equal(0, sink.EnqueuedWhileFree);
        Assert.Equal(1, sink.Releases);
    }

    [Fact]
    public async Task A_second_turn_on_the_reused_sink_is_still_buffered()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = Half(sink, tts, out _);

        output.OnText("第一回合的第一句。第一回合的第二句。", sessionEventSeq: 1);
        await output.FlushTurnAsync();

        var freeAfterFirstTurn = sink.EnqueuedWhileFree;
        var releasesAfterFirstTurn = sink.Releases;

        output.OnText("第二回合的第一句。第二回合的第二句。", sessionEventSeq: 2);
        await output.FlushTurnAsync();

        // The second turn must also play nothing before Release.
        Assert.Equal(freeAfterFirstTurn, sink.EnqueuedWhileFree);
        Assert.Equal(releasesAfterFirstTurn + 1, sink.Releases);
        Assert.Equal(
            new[] { "第一回合的第一句。", "第一回合的第二句。", "第二回合的第一句。", "第二回合的第二句。" },
            tts.Requests);
    }

    private static VoiceOutputHalf TimedHalf(IAudioSink sink, ITtsClient tts, Func<long> nowMs) =>
        new(
            new VoiceOutputSetting { Enabled = true, MuteDuringTts = false, DrainMs = 0 },
            sink,
            tts,
            new PlaybackGate(),
            nowMs: nowMs);

    [Fact]
    public async Task A_streamed_reply_starts_once_two_sentences_are_ready_not_when_it_ends()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = TimedHalf(sink, tts, () => 0);

        output.BeginStream();
        output.OnText("第一句话。第二句话。", sessionEventSeq: 1);
        await output.PlayPendingAsync();

        // No Flush yet: the reply is still being written, and the speaker is already let go.
        Assert.Equal(1, sink.Releases);
        Assert.False(output.IsStreaming);
        Assert.Equal(0, sink.EnqueuedWhileFree);

        // A later sentence plays as soon as it is synthesized.
        output.OnText("第三句话。", sessionEventSeq: 1);
        await output.PlayPendingAsync();
        Assert.Equal(2, sink.EnqueuedWhileFree); // the audio chunk and the sentence-final marker
    }

    [Fact]
    public async Task A_lone_first_sentence_waits_a_short_lead_for_a_second_then_plays_anyway()
    {
        long now = 0;
        var sink = new RecordingSink();
        using var output = TimedHalf(sink, new ScriptedTts(), () => now);

        output.BeginStream();
        output.OnText("第一句话。", sessionEventSeq: 1);
        await output.PlayPendingAsync();

        now = VoiceOutputHalf.StreamLeadMs - 1;
        output.ReleaseStreamIfDue();
        Assert.Equal(0, sink.Releases);
        Assert.True(output.IsStreaming);

        now = VoiceOutputHalf.StreamLeadMs;
        output.ReleaseStreamIfDue();
        Assert.Equal(1, sink.Releases);
        Assert.False(output.IsStreaming);
    }

    [Fact]
    public async Task A_reply_that_ends_before_the_lead_is_released_by_the_flush_exactly_once()
    {
        var sink = new RecordingSink();
        using var output = TimedHalf(sink, new ScriptedTts(), () => 0);

        output.BeginStream();
        output.OnText("好的。", sessionEventSeq: 1);
        await output.PlayPendingAsync();
        await output.FlushTurnAsync();

        Assert.Equal(1, sink.Releases);
        Assert.False(output.IsStreaming);
    }

    [Fact]
    public async Task A_stream_does_not_latch_audio_that_is_already_playing()
    {
        var sink = new RecordingSink();
        using var output = TimedHalf(sink, new ScriptedTts(), () => 0);

        // Audio of the previous step is still coming out of the speaker.
        output.OnText("上一步的话。", sessionEventSeq: 1);
        await output.PlayPendingAsync();
        Assert.True(sink.IsPlaying);

        output.BeginStream();

        Assert.False(output.IsStreaming);
    }

    /// <summary>Records whether each segment arrived while held, and counts Release calls.</summary>
    private sealed class RecordingSink : IAudioSink
    {
        private bool _held;

        public int SampleRate => 24_000;

        public string DeviceName => "recording";

        public double DrainMs { get; set; } = 400;

        public int EnqueuedSegments { get; private set; }

        public int EnqueuedWhileFree { get; private set; }

        public int Releases { get; private set; }

        public bool IsPlaying { get; private set; }

        public bool HasPending { get; private set; }

        public void Start()
        {
        }

        public void Enqueue(float[] pcm, int sampleRate, bool final = false)
        {
            ArgumentNullException.ThrowIfNull(pcm);

            // A terminator (empty array) is not an audio segment; every sentence ends with one.
            if (pcm.Length > 0)
            {
                EnqueuedSegments++;
            }

            if (!_held)
            {
                EnqueuedWhileFree++;
                IsPlaying = true;
            }

            HasPending = !final;
        }

        public void Hold() => _held = true;

        public void Release()
        {
            _held = false;
            Releases++;
        }

        public void Stop()
        {
            IsPlaying = false;
            HasPending = false;
        }

        public void Dispose() => Stop();
    }

    /// <summary>Records each synthesis request and returns a short PCM buffer in order.</summary>
    private sealed class ScriptedTts : ITtsClient
    {
        public List<string> Requests { get; } = new();

        public async IAsyncEnumerable<float[]> SynthesizeAsync(
            string text,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(text);
            yield return new float[240];
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Interrupting_stops_the_sink_and_drops_what_has_not_been_spoken_until_the_person_speaks_again()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = TimedHalf(sink, tts, () => 0);

        output.OnText("第一句话。第二句话。", sessionEventSeq: 1);
        output.StopPlayback();

        // The sentences that had not been synthesized are not spoken, and nothing more is synthesized.
        await output.PlayPendingAsync();
        Assert.Empty(tts.Requests);
        Assert.All(output.Items, item => Assert.Equal(SpeechStatus.Failed, item.Status));

        // The rest of the interrupted reply keeps arriving and is dropped as it does.
        Assert.Empty(output.OnText("第三句话。", sessionEventSeq: 1));

        // A new utterance from the person: the next reply is spoken again.
        output.ResumeAfterInterruption();
        Assert.Single(output.OnText("新的回答。", sessionEventSeq: 2));
        await output.PlayPendingAsync();
        Assert.Equal(new[] { "新的回答。" }, tts.Requests);
    }

    [Fact]
    public async Task The_configured_gap_is_silence_after_each_sentence()
    {
        var sink = new RecordingSink();
        using var output = new VoiceOutputHalf(
            new VoiceOutputSetting { Enabled = true, MuteDuringTts = false, DrainMs = 0, GapMs = 200, SampleRate = 24_000 },
            sink,
            new ScriptedTts(),
            new PlaybackGate());

        output.OnText("第一句话。", sessionEventSeq: 1);
        await output.FlushTurnAsync();

        // The sentence's own chunk plus 200 ms of silence: two non-empty segments, not one.
        Assert.Equal(2, sink.EnqueuedSegments);
    }

    [Fact]
    public void The_forced_split_length_comes_from_the_setting()
    {
        var sink = new RecordingSink();
        using var output = new VoiceOutputHalf(
            new VoiceOutputSetting { Enabled = true, MuteDuringTts = false },
            sink,
            new ScriptedTts(),
            new PlaybackGate(),
            maxSplitChars: 10);

        var items = output.OnText(new string('字', 25), sessionEventSeq: 1);

        Assert.True(items.Count >= 2, "25 characters with no punctuation should be cut at 10");
        Assert.All(items, item => Assert.True(item.Text.Length <= 10));
    }

    [Fact]
    public async Task Approval_speech_keeps_its_own_sentences_and_goes_before_unplayed_assistant_text()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = Half(sink, tts, out var gate);

        output.OnText("第一句话。第二句话。", sessionEventSeq: 1);
        output.Say(
            "需要批准：桌面操作。说「批准执行」或「拒绝执行」。",
            SpeechKind.ApprovalPrompt,
            "ap-1",
            priority: true,
            forceGate: true);

        await output.PlayPendingAsync();

        Assert.Equal(4, tts.Requests.Count);
        Assert.Contains("需要批准", tts.Requests[0], StringComparison.Ordinal);
        Assert.Contains("批准执行", tts.Requests[1], StringComparison.Ordinal);
        Assert.Equal(new[] { "第一句话。", "第二句话。" }, tts.Requests.Skip(2));

        Assert.Equal(SpeechKind.ApprovalPrompt, output.Items[0].Kind);
        Assert.Equal("ap-1", output.Items[0].Tag);
        Assert.All(
            output.Items.Where(item => item.Tag == "ap-1"),
            item => Assert.Equal(SpeechStatus.Played, item.Status));

        // The prompt's audio contains the command words, so it closes the microphone even when the output
        // setting would normally leave it open.
        Assert.True(gate.Engaged);
    }

    [Fact]
    public async Task A_dropped_prompt_is_not_synthesized()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = Half(sink, tts, out _);

        output.Say("需要批准：桌面操作。", SpeechKind.ApprovalPrompt, "ap-2", priority: true, forceGate: true);
        output.DropTag("ap-2", "请求已处理");

        await output.PlayPendingAsync();

        Assert.Empty(tts.Requests);
        Assert.All(output.Items, item => Assert.Equal(SpeechStatus.Failed, item.Status));
    }

    [Fact]
    public async Task The_time_to_first_audio_is_recorded_when_the_speaker_is_let_go()
    {
        Partner.Core.Diagnostics.LatencyTracker.Reset();
        long now = 1000;
        var sink = new RecordingSink();
        using var output = TimedHalf(sink, new ScriptedTts(), () => now);

        output.BeginStream();
        output.OnText("第一句话。第二句话。", sessionEventSeq: 1);
        now = 1700;
        await output.PlayPendingAsync();

        var first = Assert.Single(Partner.Core.Diagnostics.LatencyTracker.Snapshot(), l => l.Name == "first_audio");
        Assert.Equal(1, first.Count);
        Assert.Equal(700, first.LastMs);
    }
}
