using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Tts;

namespace Partner.Voice.Tests;

/// <summary>
/// 播放顺序与「先合成完再出声」（设计文档 §2.8.11）。
/// <para>
/// 真机症状：一回合里第 1 句刚出声，第 2 句才开始合成，于是句间插进一段随文本变长的静音。
/// 根因是闩锁只在 sink 构造时上过一次，而 sink 跨回合复用——第 2 个回合起没有任何东西挡住
/// 边合成边播。这组用例断言的是「每一句的音频都到达得比 Release 早」，也就是闩锁每回合都在。
/// </para>
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

        // 一声都没漏出去：全部到达时闩还上着，Release 在最后。
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

        // 第二个回合同样一个字都不许在 Release 前出声——这正是真机上从第二回合开始出现的那段静音。
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

    /// <summary>逐句记下「到达时闩着没有」，同时数 Release 次数。</summary>
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

            // 终止标记（空数组）不算一段音频：每句末尾都有一个。
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

    /// <summary>记下每句的合成请求，按顺序返回一小段 PCM。</summary>
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
