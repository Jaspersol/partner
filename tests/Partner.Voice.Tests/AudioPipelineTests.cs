using Partner.Voice.Audio;
using Partner.Voice.Perception;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Text;

namespace Partner.Voice.Tests;

/// <summary>Ring buffer + stream clock (design doc §2.8.1).</summary>
public sealed class AudioRingBufferTests
{
    private static float[] Tone(int samples, float amplitude = 0.5f)
    {
        var buffer = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            buffer[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / 16_000.0));
        }

        return buffer;
    }

    [Fact]
    public void Seconds_is_the_true_stream_clock()
    {
        var ring = new AudioRingBuffer(2, 16_000);

        ring.Write(Tone(16_000));
        ring.Write(Tone(8_000));

        Assert.Equal(1.5, ring.Seconds, 3);
    }

    [Fact]
    public void Read_since_returns_the_requested_window()
    {
        var ring = new AudioRingBuffer(4, 16_000);
        ring.Write(Tone(16_000)); // 1 second

        var audio = ring.ReadSince(0.25, 0.5);

        Assert.NotNull(audio);
        Assert.Equal(8_000, audio!.Length);
    }

    [Fact]
    public void Read_since_returns_null_once_the_cursor_is_evicted()
    {
        var ring = new AudioRingBuffer(1, 16_000);
        ring.Write(Tone(32_000)); // 2 seconds into a 1 second buffer

        Assert.Null(ring.ReadSince(0.1, 0.5));
        Assert.Equal(1.0, ring.OldestAvailableSeconds, 3);
    }

    [Fact]
    public void Energy_tracks_amplitude_and_silence_is_zero()
    {
        var ring = new AudioRingBuffer(2, 16_000);

        ring.Write(new float[16_000]);
        Assert.Equal(0, ring.Energy(0.5), 5);

        ring.Write(Tone(16_000, 0.5f));
        Assert.InRange(ring.Energy(0.5), 0.3, 0.4); // RMS of a sine at 0.5 amplitude ≈ 0.354
    }

    [Fact]
    public void Speech_fraction_measures_voiced_20ms_frames()
    {
        var ring = new AudioRingBuffer(2, 16_000);
        ring.Write(new float[8_000]);      // 0.5 s silence
        ring.Write(Tone(8_000, 0.5f));     // 0.5 s speech

        var fraction = ring.SpeechFraction(0.1, 1.0);

        Assert.InRange(fraction, 0.4, 0.6);
    }

    [Fact]
    public void A_write_larger_than_the_buffer_keeps_the_newest_tail()
    {
        var ring = new AudioRingBuffer(1, 16_000);
        ring.Write(Tone(48_000));

        Assert.Equal(3.0, ring.Seconds, 3);            // the clock still counts everything written
        Assert.Equal(2.0, ring.OldestAvailableSeconds, 3);
        Assert.Equal(16_000, ring.ReadSince(2.0, 1.0)!.Length);
    }
}

/// <summary>Pause blocking (design doc §2.8.2/§2.8.3): the two knobs and the owed-block retry.</summary>
public sealed class PauseBlockingDetectorTests
{
    private static PauseBlockingDetector Detector(bool stream = false, double threshold = 0.03) => new(
        new VoiceInputSetting
        {
            SilenceThreshold = threshold,
            SentenceGapMs = 450,
            UtteranceGapMs = 1200,
            WindowSeconds = 8,
            AsrStream = stream,
        });

    [Fact]
    public void Speech_then_a_short_pause_does_not_end_the_utterance()
    {
        var detector = Detector();

        Assert.False(detector.Step(0.0, 0.5).Submit);   // speech
        Assert.False(detector.Step(0.2, 0.5).Submit);
        Assert.True(detector.Dirty);

        // 500 ms of silence: past G1 (450) but well short of G2 (1200).
        Assert.False(detector.Step(0.7, 0.0).Submit);
    }

    [Fact]
    public void Silence_offset_is_added_back_so_the_gap_means_the_real_pause()
    {
        var detector = Detector();
        detector.Step(0.0, 0.5);   // speech
        detector.Step(0.2, 0.0);   // the pause starts here

        // Measured silence of 1100 ms (0.2 → 1.3) plus the 100 ms offset reaches G2.
        var decision = detector.Step(1.3, 0.0);

        Assert.True(decision.Submit);
        Assert.Equal(BlockTrigger.UtteranceGap, decision.Trigger);
        Assert.True(decision.Pause);
    }

    [Fact]
    public void G1_only_fires_in_streaming_mode()
    {
        var batch = Detector(stream: false);
        batch.Step(0.0, 0.5);
        batch.Step(0.2, 0.0);
        Assert.Equal(BlockTrigger.None, batch.Step(0.7, 0.0).Trigger);

        var stream = Detector(stream: true);
        stream.Step(0.0, 0.5);
        stream.Step(0.2, 0.0);
        var decision = stream.Step(0.7, 0.0);

        Assert.Equal(BlockTrigger.SentenceGap, decision.Trigger);
        Assert.True(decision.Pause);
    }

    [Fact]
    public void Continuous_speech_is_cut_at_the_window_cap()
    {
        var detector = Detector();

        detector.Step(0.0, 0.5);
        var decision = detector.Step(8.5, 0.5);

        Assert.Equal(BlockTrigger.WindowCap, decision.Trigger);
        Assert.False(decision.Pause);
    }

    [Fact]
    public void A_pause_with_no_speech_never_submits()
    {
        var detector = Detector();

        for (var t = 0.0; t < 5.0; t += 0.1)
        {
            Assert.False(detector.Step(t, 0.0).Submit);
        }

        Assert.False(detector.Dirty);
    }

    [Fact]
    public void A_busy_outcome_keeps_the_cursor_and_the_pause_flag()
    {
        var detector = Detector();
        detector.Step(0.0, 0.5);
        detector.Step(0.2, 0.0);
        var decision = detector.Step(1.4, 0.0);
        Assert.True(decision.Submit);

        detector.OnSubmitted(decision.BlockEnd, decision.Pause, SubmissionOutcome.Busy);

        Assert.True(detector.HasHeldPause);
        Assert.Equal(0, detector.Cursor); // the audio is still owed, so the cursor did not move

        // The next tick retries the same block instead of moving on.
        var retry = detector.Step(1.5, 0.0);
        Assert.True(retry.Submit);
        Assert.True(retry.Pause);
        Assert.Equal(0, retry.BlockStart);
    }

    [Fact]
    public void A_queued_outcome_advances_the_cursor()
    {
        var detector = Detector();
        detector.Step(0.0, 0.5);
        detector.Step(0.2, 0.0);
        var decision = detector.Step(1.4, 0.0);
        Assert.True(decision.Submit);

        detector.OnSubmitted(decision.BlockEnd, decision.Pause, SubmissionOutcome.Queued);

        Assert.False(detector.HasHeldPause);
        Assert.Equal(1.4, detector.Cursor, 3);
    }

    [Fact]
    public void Segment_end_and_gate_release_reset_the_segment_state()
    {
        var detector = Detector();
        detector.Step(0.0, 0.5);
        Assert.True(detector.Dirty);

        detector.OnSegmentEnded(2.0);

        Assert.False(detector.Dirty);
        Assert.Equal(2.0, detector.Cursor, 3);

        detector.Step(3.0, 0.5);
        detector.OnGateReleased(4.0);

        Assert.Equal(4.0, detector.Cursor, 3);
        Assert.False(detector.HasLeftover);
    }

    [Fact]
    public void Silence_before_any_speech_keeps_the_cursor_at_now()
    {
        var detector = Detector();

        detector.Step(0.0, 0.0);
        detector.Step(5.0, 0.0);

        // A pause longer than the ring would otherwise age the cursor out and report a bogus loss.
        Assert.Equal(5.0, detector.Cursor, 3);
    }
}

/// <summary>The gate: seven conditions, and "why is it still closed" must be answerable.</summary>
public sealed class PlaybackGateTests
{
    private static GateConditions Clear => new(
        PendingBlock: false,
        IsPlaying: false,
        PendingAudio: false,
        SentencesEmpty: true,
        SegmenterEmpty: true,
        TtsBusy: false,
        AsrBusy: false);

    [Fact]
    public void All_seven_conditions_must_clear()
    {
        Assert.True(Clear.AllClear);

        // The one that cannot be inferred from the others: a submitted-but-not-absorbed block.
        Assert.False((Clear with { PendingBlock = true }).AllClear);
        Assert.False((Clear with { IsPlaying = true }).AllClear);
        Assert.False((Clear with { PendingAudio = true }).AllClear);
        Assert.False((Clear with { SentencesEmpty = false }).AllClear);
        Assert.False((Clear with { SegmenterEmpty = false }).AllClear);
        Assert.False((Clear with { TtsBusy = true }).AllClear);
        Assert.False((Clear with { AsrBusy = true }).AllClear);
    }

    [Fact]
    public void Blockers_name_every_closed_condition()
    {
        var conditions = new GateConditions(true, true, true, false, false, true, true);

        Assert.Equal(
            new[] { "pending-block", "playing", "pending-audio", "sentences", "segmenter", "tts-busy", "asr-busy" },
            conditions.Blockers);
    }

    [Fact]
    public void Engaged_gate_stays_closed_until_everything_drains()
    {
        var gate = new PlaybackGate();
        gate.Engage(0);

        Assert.False(gate.TryRelease(Clear with { IsPlaying = true }, 1));
        Assert.Equal("playing", gate.LastBlockers);
        Assert.True(gate.Engaged);

        Assert.True(gate.TryRelease(Clear, 2));
        Assert.False(gate.Engaged);
        Assert.Null(gate.LastBlockers);
    }

    [Fact]
    public void Skipped_audio_is_accounted_for_and_flagged_when_it_contained_speech()
    {
        var gate = new PlaybackGate();
        gate.Engage(0);
        gate.NoteSkipped(1.5, hadSpeech: true);
        gate.NoteSkipped(0.5, hadSpeech: false);

        Assert.Equal(2.0, gate.SkippedSeconds, 3);
        Assert.True(gate.SkippedHadSpeech);

        gate.ResetSkipped();
        Assert.Equal(0, gate.SkippedSeconds);
        Assert.False(gate.SkippedHadSpeech);
    }

    [Fact]
    public void A_released_gate_releases_immediately()
    {
        var gate = new PlaybackGate();

        Assert.True(gate.TryRelease(Clear, 0));
        Assert.False(gate.Engaged);
    }
}

/// <summary>Sentence splitting and readability cleaning (design doc §2.8.11).</summary>
public sealed class SpeechTextTests
{
    [Fact]
    public void Sentences_split_on_endings_and_flush_the_tail()
    {
        var segmenter = new SpeechSegmenter();

        var first = segmenter.Push("第一句话。第二句话");
        Assert.Equal(new[] { "第一句话。" }, first);

        var tail = segmenter.Flush();
        Assert.Equal(new[] { "第二句话" }, tail);
        Assert.True(segmenter.IsEmpty);
    }

    [Fact]
    public void Long_unpunctuated_text_is_cut_at_a_soft_break_then_hard()
    {
        var segmenter = new SpeechSegmenter(maxChars: 10);

        var ready = segmenter.Push("一二三四五六七八九十十一十二");

        Assert.NotEmpty(ready);
        Assert.All(ready, s => Assert.True(s.Length <= 10, s));
    }

    [Fact]
    public void Empty_push_yields_nothing()
    {
        var segmenter = new SpeechSegmenter();

        Assert.Empty(segmenter.Push(string.Empty));
        Assert.Empty(segmenter.Flush());
    }

    [Fact]
    public void Cleaner_strips_markdown_emoji_links_and_code_fences()
    {
        const string markdown = """
            ## Heading

            - bullet item **bold** and `code`
            [label](https://example.com/page) 🎉

            ```csharp
            var x = 1;
            ```
            """;

        var cleaned = TtsTextCleaner.Clean(markdown);

        Assert.Contains("Heading", cleaned, StringComparison.Ordinal);
        Assert.Contains("bullet item", cleaned, StringComparison.Ordinal);
        Assert.Contains("bold", cleaned, StringComparison.Ordinal);
        Assert.Contains("label", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("**", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("`", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("var x = 1;", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("#", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("🎉", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleaner_collapses_whitespace_and_handles_empty_input()
    {
        Assert.Equal("a b", TtsTextCleaner.CollapseWhitespace("a \n\n  b "));
        Assert.Equal(string.Empty, TtsTextCleaner.Clean("   "));
    }
}
