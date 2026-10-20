using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Text;
using Partner.Voice.Tts;

namespace Partner.Voice;

public enum SpeechStatus
{
    Queued,
    Synthesizing,
    Ready,
    Playing,
    Played,

    /// <summary>Synthesis failed: a zero-length tombstone is still emitted so the player never stalls.</summary>
    Failed,
}

/// <summary>One sentence on its way to the speaker.</summary>
public sealed record SpeechItem
{
    public required string SpeechId { get; init; }

    public required int Seq { get; init; }

    /// <summary>The session event this sentence came from, so "what was spoken" is traceable.</summary>
    public required long SessionEventSeq { get; init; }

    public required string Text { get; init; }

    public required SpeechStatus Status { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Output half: session text → readable sentences → streaming TTS → ordered playback,
/// with the gate closed while speaking. Only exists when voice output is enabled.
/// </summary>
public sealed class VoiceOutputHalf : IDisposable
{
    private readonly VoiceOutputSetting _settings;
    private readonly IAudioSink _sink;
    private readonly ITtsClient _tts;
    private readonly PlaybackGate _gate;
    private readonly Action<string> _log;
    private readonly List<SpeechItem> _items = new();
    private readonly Func<long> _nowMs;
    private int _seq;

    // Streaming start: audio is latched until the first sentence (or a short lead) is ready, see BeginStream.
    private bool _streamHeld;
    private int _streamSynthesized;
    private volatile bool _interrupted;
    private long _replyStartedMs;
    private bool _replyOpen;
    private bool _replyTimed;
    private long _streamFirstDoneMs;

    /// <summary>
    /// How long the first synthesized sentence may wait for a second one before playback starts anyway. Holding
    /// for the second sentence is what keeps the join between the two seamless; holding longer than this would
    /// trade a short gap for a long silence at the start, which is the thing streaming exists to remove.
    /// </summary>
    public const int StreamLeadMs = 1500;

    public VoiceOutputHalf(
        VoiceOutputSetting settings,
        IAudioSink sink,
        ITtsClient tts,
        PlaybackGate gate,
        Action<string>? log = null,
        Func<long>? nowMs = null,
        int maxSplitChars = 40)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _log = log ?? (_ => { });

        _sink.DrainMs = settings.DrainMs;
        Segmenter = new SpeechSegmenter(Math.Max(1, maxSplitChars));
        Enabled = settings.Enabled;
    }

    public bool Enabled { get; set; }

    public SpeechSegmenter Segmenter { get; }

    public IReadOnlyList<SpeechItem> Items => _items;

    public bool TtsBusy { get; private set; }

    public bool IsPlaying => _sink.IsPlaying;

    public bool PendingAudio => _sink.HasPending;

    /// <summary>True between <see cref="BeginStream"/> and the playback release that ends the hold.</summary>
    public bool IsStreaming => _streamHeld;

    public bool SentencesEmpty => Segmenter.IsEmpty && _items.All(i => i.Status is SpeechStatus.Played or SpeechStatus.Failed);

    /// <summary>Feeds assistant text and returns the sentences that became speakable.</summary>
    public IReadOnlyList<SpeechItem> OnText(string text, long sessionEventSeq)
    {
        if (_interrupted || !Enabled || string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<SpeechItem>();
        }

        var cleaned = TtsTextCleaner.Clean(text);
        if (cleaned.Length == 0)
        {
            return Array.Empty<SpeechItem>();
        }

        if (!_replyOpen)
        {
            _replyOpen = true;
            _replyStartedMs = _nowMs();
            _replyTimed = false;
        }

        var ready = Segmenter.Push(cleaned);
        var items = new List<SpeechItem>();

        foreach (var sentence in DeduplicateAgainstPending(ready))
        {
            var item = new SpeechItem
            {
                SpeechId = Guid.NewGuid().ToString("n"),
                Seq = ++_seq,
                SessionEventSeq = sessionEventSeq,
                Text = sentence,
                Status = SpeechStatus.Queued,
            };

            _items.Add(item);
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Starts a reply that is being spoken while the model is still writing it. Audio is latched, the first
    /// sentence is synthesized, and playback is released as soon as that sentence — plus a second one, or
    /// <see cref="StreamLeadMs"/> of waiting — is in the sink. Later sentences are synthesized while earlier
    /// ones play.
    /// <para>
    /// When audio is already playing (a later step of the same turn) nothing is latched: the sink's own backlog
    /// already covers the synthesis time of the next sentence.
    /// </para>
    /// </summary>
    public void BeginStream()
    {
        if (!Enabled || _streamHeld || _sink.IsPlaying || _sink.HasPending)
        {
            return;
        }

        _sink.Hold();
        _streamHeld = true;
        _streamSynthesized = 0;
        _streamFirstDoneMs = 0;
    }

    /// <summary>
    /// Stops speaking now: playback ends, sentences still waiting to be synthesized are dropped, and the
    /// half is ready for the next reply. The sentences already played stay in <see cref="Items"/> as played.
    /// </summary>
    public void StopPlayback()
    {
        // Set first: a synthesis already running on the output loop checks it before it hands the sink more audio.
        _interrupted = true;
        _replyOpen = false;
        _sink.Stop();
        _streamHeld = false;
        _streamSynthesized = 0;

        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Status is SpeechStatus.Queued or SpeechStatus.Synthesizing)
            {
                _items[i] = _items[i] with { Status = SpeechStatus.Failed, Error = "被用户打断" };
            }
        }

        // The unfinished tail of the reply is not spoken either.
        Segmenter.Flush();
    }

    /// <summary>
    /// The person has spoken again, so the next reply is an answer to something new and is spoken normally. Until
    /// then the rest of an interrupted reply is dropped as it arrives.
    /// </summary>
    public void ResumeAfterInterruption() => _interrupted = false;

    /// <summary>Releases a held stream whose first sentence has waited long enough for a second one.</summary>
    public void ReleaseStreamIfDue()
    {
        if (_streamHeld && _streamSynthesized >= 1 && _nowMs() - _streamFirstDoneMs >= StreamLeadMs)
        {
            ReleaseStream();
        }
    }

    private void ReleaseStream()
    {
        if (!_streamHeld)
        {
            return;
        }

        NoteFirstAudio();

        _streamHeld = false;
        _sink.Release();

        // Audio is about to come out of the speaker, so the microphone closes with it — not at the end of the
        // reply, when a streamed first sentence would already have been heard by the capture path.
        if (_settings.MuteDuringTts)
        {
            _gate.Engage();
        }
    }

    /// <summary>
    /// Ends the turn: flushes the trailing fragment, starts playback and closes the gate.
    /// <para>
    /// The latch is set here rather than in the sink constructor: the sink is reused across turns, so a
    /// constructor-time value only applies to the first turn.
    /// </para>
    /// </summary>
    public async Task FlushTurnAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return;
        }

        // Latch again on every turn: all sentences of the turn are synthesized into the sink queue first and
        // released together. Otherwise sentence 2 waits for sentence 1 to finish synthesizing, leaving a gap
        // that grows with the text. The tone uses the same sink, so while latched it only queues.
        if (!_streamHeld)
        {
            // A stream that is already playing was released earlier; latching again would stall it.
            if (!_sink.IsPlaying && !_sink.HasPending)
            {
                _sink.Hold();
            }
        }

        foreach (var sentence in DeduplicateAgainstPending(Segmenter.Flush()))
        {
            _items.Add(new SpeechItem
            {
                SpeechId = Guid.NewGuid().ToString("n"),
                Seq = ++_seq,
                SessionEventSeq = _items.Count == 0 ? 0 : _items[^1].SessionEventSeq,
                Text = sentence,
                Status = SpeechStatus.Queued,
            });
        }

        await PlayPendingAsync(ct).ConfigureAwait(false);

        NoteFirstAudio();
        _replyOpen = false;

        _streamHeld = false;
        _sink.Release();

        if (_settings.MuteDuringTts && _items.Any(i => i.Status is SpeechStatus.Played or SpeechStatus.Playing))
        {
            _gate.Engage();
        }
    }

    /// <summary>Synthesizes and enqueues every queued sentence, strictly in sequence order.</summary>
    public async Task<int> PlayPendingAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return 0;
        }

        _sink.Start();
        var played = 0;

        while (true)
        {
            var queued = _interrupted ? null : _items.FirstOrDefault(i => i.Status == SpeechStatus.Queued);
            if (queued is null)
            {
                break;
            }

            // Records are immutable, so every state change replaces the entry — and it must be found by
            // SpeechId, not by instance: the previous instance is no longer in the list.
            var item = queued with { Status = SpeechStatus.Synthesizing };
            Replace(item);
            TtsBusy = true;

            try
            {
                await foreach (var chunk in _tts.SynthesizeAsync(item.Text, ct).ConfigureAwait(false))
                {
                    if (_interrupted)
                    {
                        break;
                    }

                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    _sink.Enqueue(chunk, _settings.SampleRate);
                }

                // The configured pause between sentences, as silence at the end of this one.
                if (_settings.GapMs > 0)
                {
                    _sink.Enqueue(new float[(int)(_settings.SampleRate * _settings.GapMs / 1000.0)], _settings.SampleRate);
                }

                // A final marker is mandatory: without it the player waits for this sequence forever.
                _sink.Enqueue(Array.Empty<float>(), _settings.SampleRate, final: true);

                Replace(item with { Status = SpeechStatus.Played });
                played++;
                NoteSynthesized();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Tombstone: the sequence still terminates so later sentences are not dragged down.
                _sink.Enqueue(Array.Empty<float>(), _settings.SampleRate, final: true);
                Replace(item with { Status = SpeechStatus.Failed, Error = ex.Message });
                NoteSynthesized();
                _log($"voice: synthesis failed for '{item.Text}': {ex.Message}");
            }
            finally
            {
                TtsBusy = false;
            }
        }

        return played;
    }

    /// <summary>First sound of a reply: from its first text reaching this half to the speaker being let go.</summary>
    private void NoteFirstAudio()
    {
        if (_replyOpen && !_replyTimed)
        {
            _replyTimed = true;
            Partner.Core.Diagnostics.LatencyTracker.Record("first_audio", Math.Max(0, _nowMs() - _replyStartedMs));
        }
    }

    private void NoteSynthesized()
    {
        if (!_streamHeld)
        {
            return;
        }

        _streamSynthesized++;
        if (_streamSynthesized == 1)
        {
            _streamFirstDoneMs = _nowMs();
        }

        if (_streamSynthesized >= 2 || _nowMs() - _streamFirstDoneMs >= StreamLeadMs)
        {
            ReleaseStream();
        }
    }

    /// <summary>The seven gate conditions, filled in from the output half's own state.</summary>
    public GateConditions Conditions(bool pendingBlock, bool asrBusy) => new(
        PendingBlock: pendingBlock,
        IsPlaying: IsPlaying,
        PendingAudio: PendingAudio,
        SentencesEmpty: SentencesEmpty,
        SegmenterEmpty: Segmenter.IsEmpty,
        TtsBusy: TtsBusy,
        AsrBusy: asrBusy);

    /// <summary>Attempts to reopen the gate; returns true and logs when it actually reopened.</summary>
    public bool TryReleaseGate(bool pendingBlock, bool asrBusy, double nowSeconds)
    {
        var released = _gate.TryRelease(Conditions(pendingBlock, asrBusy), nowSeconds);
        if (released && _gate.LastReleasedAt is not null)
        {
            _gate.ResetSkipped();
        }

        return released;
    }

    public void Dispose()
    {
        _tts.Dispose();
        _sink.Dispose();
    }

    /// <summary>Replaces an entry by its stable id — records are immutable, so instance identity is useless.</summary>
    private void Replace(SpeechItem updated)
    {
        var index = _items.FindIndex(i => string.Equals(i.SpeechId, updated.SpeechId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _items[index] = updated;
        }
    }

    /// <summary>
    /// Same-text sentence deduplication: a new sentence identical to one still playing or still queued is
    /// dropped. When assistant text arrives twice (retries, log replay), the listener would otherwise hear
    /// "first segment half-played, then the second from the start". Blocking at the synthesis layer is cheaper
    /// than hunting two players at the playback layer.
    /// </summary>
    private IReadOnlyList<string> DeduplicateAgainstPending(IReadOnlyList<string> sentences)
    {
        if (sentences.Count == 0)
        {
            return sentences;
        }

        var result = new List<string>(sentences.Count);
        foreach (var sentence in sentences)
        {
            // Comparing against the most recent unfinished sentence is enough: duplicates come from replaying the same text through the pipeline.
            var duplicate = _items
                .Where(i => i.Status is not (SpeechStatus.Played or SpeechStatus.Failed))
                .Any(i => string.Equals(i.Text, sentence, StringComparison.Ordinal));
            if (!duplicate)
            {
                result.Add(sentence);
            }
        }

        return result;
    }
}
