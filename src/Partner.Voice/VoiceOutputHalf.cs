using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Text;
using Partner.Voice.Tts;

namespace Partner.Voice;

/// <summary>Where a sentence came from. Approval speech is not part of the conversation and must not share
/// the assistant's segmenter or its duplicate suppression.</summary>
public enum SpeechKind
{
    Assistant,
    ApprovalPrompt,
    ApprovalResult,
}

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

    /// <summary>Assistant text by default; approval prompts and their results are marked so they can be found
    /// and dropped by their <see cref="Tag"/>.</summary>
    public SpeechKind Kind { get; init; } = SpeechKind.Assistant;

    /// <summary>The approval id this item belongs to, when it is not assistant text.</summary>
    public string? Tag { get; init; }
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
    private readonly object _itemsGate = new();
    private readonly LinkedList<SpeechItem> _active = new();
    private readonly Dictionary<string, LinkedListNode<SpeechItem>> _activeById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingText = new(StringComparer.Ordinal);
    private readonly Queue<SpeechItem> _history = new();
    private const int HistoryLimit = 256;
    private int _playbackGeneration;
    private CancellationTokenSource _generationStop = new();
    private long _lastAssistantEventSeq;
    private readonly Func<long> _nowMs;
    private readonly int _maxSplitChars;
    private readonly int _minChars;
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
        Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _log = log ?? (_ => { });

        _sink.DrainMs = settings.DrainMs;
        _maxSplitChars = Math.Max(1, settings.MaxSplitChars);
        _minChars = Math.Max(1, settings.MinSentenceChars);
        Segmenter = new SpeechSegmenter(_maxSplitChars, _minChars);
        Enabled = settings.Enabled;
    }

    public bool Enabled { get; set; }

    public SpeechSegmenter Segmenter { get; }

    public IReadOnlyList<SpeechItem> Items { get { lock (_itemsGate) return _history.Concat(_active).ToArray(); } }

    /// <summary>Total accepted sentences, including records evicted from diagnostic history.</summary>
    public int TotalCount => _seq;

    public bool TtsBusy { get; private set; }

    public bool IsPlaying => _sink.IsPlaying;

    public bool PendingAudio => _sink.HasPending;

    /// <summary>True between <see cref="BeginStream"/> and the playback release that ends the hold.</summary>
    public bool IsStreaming => _streamHeld;

    public bool SentencesEmpty { get { lock (_itemsGate) return Segmenter.IsEmpty && _active.Count == 0; } }

    /// <summary>Feeds assistant text and returns the sentences that became speakable.</summary>
    public IReadOnlyList<SpeechItem> OnText(string text, long sessionEventSeq)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text))
            return Array.Empty<SpeechItem>();

        var generation = Volatile.Read(ref _playbackGeneration);
        var cleaned = TtsTextCleaner.Clean(text);
        if (cleaned.Length == 0)
        {
            return Array.Empty<SpeechItem>();
        }

        lock (_itemsGate)
        {
        if (_interrupted || generation != _playbackGeneration) return Array.Empty<SpeechItem>();
        if (!_replyOpen)
        {
            _replyOpen = true;
            _replyStartedMs = _nowMs();
            _replyTimed = false;
        }

        var ready = Segmenter.Push(cleaned);
        _lastAssistantEventSeq = sessionEventSeq;
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

            AddActive(item);
            items.Add(item);
        }

        return items;
        }
    }

    /// <summary>
    /// Adds text that is not part of the assistant's reply — an approval prompt or its result.
    /// <para>
    /// It gets its own segmenter, so a prompt can never be joined to a half-written assistant sentence, and it
    /// bypasses <see cref="DeduplicateAgainstPending"/> because approval ids, not text, decide what was already
    /// announced. <paramref name="priority"/> puts the item before sentences that have not started playing yet:
    /// an approval has a 120-second lifetime, and waiting behind a long reply can spend it before it is heard.
    /// </para>
    /// <para>
    /// <paramref name="forceGate"/> is for approval speech. It closes the microphone even when
    /// <see cref="VoiceOutputSetting.MuteDuringTts"/> is off: the prompt itself contains "批准执行", and a
    /// speaker heard by the microphone must never turn into the answer to its own question.
    /// </para>
    /// </summary>
    public IReadOnlyList<SpeechItem> Say(
        string text,
        SpeechKind kind,
        string? tag = null,
        bool priority = false,
        bool forceGate = false)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<SpeechItem>();
        }

        var cleaned = TtsTextCleaner.Clean(text);
        if (cleaned.Length == 0)
        {
            return Array.Empty<SpeechItem>();
        }

        // Approval speech does not change the interrupted assistant generation.

        if (forceGate)
        {
            _gate.Engage();
        }

        var segmenter = new SpeechSegmenter(_maxSplitChars, _minChars);
        var sentences = new List<string>(segmenter.Push(cleaned));
        sentences.AddRange(segmenter.Flush());

        var items = new List<SpeechItem>(sentences.Count);

        // A result answers something the person just said, so it goes before every queued item. A prompt is
        // inserted before assistant text but after prompts that arrived earlier: reversing two pending
        // questions would make the order on screen disagree with the order in the speaker.
        lock (_itemsGate)
        {
        var before = _active.First;
        if (priority)
        {
            while (before is not null && (before.Value.Status != SpeechStatus.Queued
                || (kind != SpeechKind.ApprovalResult && before.Value.Kind != SpeechKind.Assistant)))
                before = before.Next;
        }
        else before = null;

        foreach (var sentence in sentences)
        {
            var item = new SpeechItem
            {
                SpeechId = Guid.NewGuid().ToString("n"),
                Seq = ++_seq,
                SessionEventSeq = 0,
                Text = sentence,
                Status = SpeechStatus.Queued,
                Kind = kind,
                Tag = tag,
            };

            AddActive(item, before);
            items.Add(item);
        }
        }

        return items;
    }

    /// <summary>
    /// Removes a prompt whose request was answered elsewhere (the banner, or an earlier voice command) or
    /// expired. Only items that have not been synthesized can be dropped; one already in the sink finishes,
    /// which is harmless because the answer path treats the request as settled.
    /// </summary>
    public void DropTag(string tag, string reason)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        lock (_itemsGate)
        {
        var node = _active.First;
        while (node is not null)
        {
            var next = node.Next;
            var item = node.Value;
            if (item.Status == SpeechStatus.Queued && string.Equals(item.Tag, tag, StringComparison.Ordinal))
                Replace(item with { Status = SpeechStatus.Failed, Error = reason });
            node = next;
        }
        }
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
        lock (_itemsGate)
        {
            _interrupted = true;
            Interlocked.Increment(ref _playbackGeneration);
            var previous = _generationStop;
            _generationStop = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            _replyOpen = false;
            _streamHeld = false;
            _streamSynthesized = 0;
            while (_active.First is { } node)
                Replace(node.Value with { Status = SpeechStatus.Failed, Error = "被用户打断" });
            Segmenter.Flush();
            _sink.Stop();
        }
    }

    /// <summary>
    /// The person has spoken again, so the next reply is an answer to something new and is spoken normally. Until
    /// then the rest of an interrupted reply is dropped as it arrives.
    /// </summary>
    public void ResumeAfterInterruption() { lock (_itemsGate) _interrupted = false; }

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
    public async Task FlushTurnAsync(CancellationToken ct = default, Action? sentenceBoundary = null)
    {
        if (!Enabled)
        {
            return;
        }
        var generation = Volatile.Read(ref _playbackGeneration);

        // A bounded sink cannot retain an entire turn while held: release before synthesis so long
        // sentences and turns can make progress under backpressure.
        if (!_streamHeld)
        {
            _sink.Release();
            if (_settings.MuteDuringTts) _gate.Engage();
        }

        lock (_itemsGate)
        {
        if (!_interrupted)
        {
        foreach (var sentence in DeduplicateAgainstPending(Segmenter.Flush()))
        {
            AddActive(new SpeechItem
            {
                SpeechId = Guid.NewGuid().ToString("n"),
                Seq = ++_seq,
                SessionEventSeq = _lastAssistantEventSeq,
                Text = sentence,
                Status = SpeechStatus.Queued,
            });
        }
        }
        }

        await PlayPendingAsync(ct, sentenceBoundary).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _playbackGeneration)) return;

        NoteFirstAudio();
        _replyOpen = false;

        _streamHeld = false;
        _sink.Release();

        if (_settings.MuteDuringTts && Items.Any(i => i.Status == SpeechStatus.Played))
        {
            _gate.Engage();
        }
    }

    /// <summary>Synthesizes and enqueues every queued sentence, strictly in sequence order.</summary>
    public async Task<int> PlayPendingAsync(CancellationToken ct = default, Action? sentenceBoundary = null)
    {
        if (!Enabled)
        {
            return 0;
        }

        _sink.Start();
        var played = 0;

        while (true)
        {
            sentenceBoundary?.Invoke();
            SpeechItem item;
            int generation;
            CancellationToken generationToken;
            lock (_itemsGate)
            {
                var queued = _active.First?.Value;
                if (queued is null) break;
                generation = Volatile.Read(ref _playbackGeneration);
                generationToken = _generationStop.Token;
                item = queued with { Status = SpeechStatus.Synthesizing };
                Replace(item);
            }
            using var synthesisStop = CancellationTokenSource.CreateLinkedTokenSource(ct, generationToken);
            TtsBusy = true;

            try
            {
                await foreach (var chunk in _tts.SynthesizeAsync(item.Text, synthesisStop.Token).ConfigureAwait(false))
                {
                    if (generation != Volatile.Read(ref _playbackGeneration))
                    {
                        break;
                    }

                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    await EnqueueAudioAsync(chunk, synthesisStop.Token).ConfigureAwait(false);
                }
                if (generation != Volatile.Read(ref _playbackGeneration)) break;


                // The configured pause between sentences, as silence at the end of this one.
                if (_settings.GapMs > 0)
                {
                    await EnqueueAudioAsync(new float[(int)(_settings.SampleRate * _settings.GapMs / 1000.0)], synthesisStop.Token).ConfigureAwait(false);
                }

                // A final marker is mandatory: without it the player waits for this sequence forever.
                await EnqueueAudioAsync(Array.Empty<float>(), synthesisStop.Token, final: true).ConfigureAwait(false);

                Replace(item with { Status = SpeechStatus.Played });
                played++;
                NoteSynthesized();
            }
            catch (OperationCanceledException) when ((_interrupted || generation != Volatile.Read(ref _playbackGeneration)) && !ct.IsCancellationRequested)
            {
                break;
            }
            catch (AudioPlaybackException)
            {
                Replace(item with { Status = SpeechStatus.Failed, Error = "音频播放失败" });
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Tombstone: the sequence still terminates so later sentences are not dragged down.
                await EnqueueAudioAsync(Array.Empty<float>(), synthesisStop.Token, final: true).ConfigureAwait(false);
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
        SegmenterEmpty: SentencesEmpty,
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
        _generationStop.Dispose();
        _tts.Dispose();
        _sink.Dispose();
    }

    private void AddActive(SpeechItem item, LinkedListNode<SpeechItem>? before = null)
    {
        lock (_itemsGate)
        {
        var node = before is null ? _active.AddLast(item) : _active.AddBefore(before, item);
        _activeById.Add(item.SpeechId, node);
        if (item.Kind == SpeechKind.Assistant)
            _pendingText[item.Text] = _pendingText.GetValueOrDefault(item.Text) + 1;
        }
    }

    /// <summary>Updates an active record by stable id, moving terminal records into bounded history.</summary>
    private void Replace(SpeechItem updated)
    {
        lock (_itemsGate)
        {
        if (!_activeById.TryGetValue(updated.SpeechId, out var node)) return;
        if (updated.Status is not (SpeechStatus.Played or SpeechStatus.Failed))
        {
            node.Value = updated;
            return;
        }
        _active.Remove(node);
        _activeById.Remove(updated.SpeechId);
        if (updated.Kind == SpeechKind.Assistant)
        {
            var count = _pendingText[updated.Text] - 1;
            if (count == 0) _pendingText.Remove(updated.Text);
            else _pendingText[updated.Text] = count;
        }
        _history.Enqueue(updated);
        if (_history.Count > HistoryLimit) _history.Dequeue();
        }
    }

    private async Task EnqueueAudioAsync(float[] pcm, CancellationToken ct, bool final = false)
    {
        try
        {
            var pending = _sink.EnqueueAsync(pcm, _settings.SampleRate, final, ct);
            if (_streamHeld && !pending.IsCompleted)
            {
                // The first chunk can itself exceed the sink capacity. Never wait for a second sentence
                // while a held producer is unable to finish writing the first one.
                if (await Task.WhenAny(pending, Task.Delay(StreamLeadMs, ct)).ConfigureAwait(false) != pending)
                    ReleaseStream();
            }
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new AudioPlaybackException(ex); }
    }

    private sealed class AudioPlaybackException(Exception inner) : Exception("Audio playback failed.", inner) { }

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
            bool duplicate;
            lock (_itemsGate) duplicate = _pendingText.ContainsKey(sentence);
            if (!duplicate)
            {
                result.Add(sentence);
            }
        }

        return result;
    }
}
