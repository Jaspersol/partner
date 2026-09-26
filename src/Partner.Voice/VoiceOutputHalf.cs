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

/// <summary>One sentence on its way to the speaker (design doc §2.2).</summary>
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
/// Output half (design doc §2.8.11): session text → readable sentences → streaming TTS → ordered playback,
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
    private int _seq;

    public VoiceOutputHalf(
        VoiceOutputSetting settings,
        IAudioSink sink,
        ITtsClient tts,
        PlaybackGate gate,
        Action<string>? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _log = log ?? (_ => { });

        _sink.DrainMs = settings.DrainMs;
        Segmenter = new SpeechSegmenter();
        Enabled = settings.Enabled;
    }

    public bool Enabled { get; set; }

    public SpeechSegmenter Segmenter { get; }

    public IReadOnlyList<SpeechItem> Items => _items;

    public bool TtsBusy { get; private set; }

    public bool IsPlaying => _sink.IsPlaying;

    public bool PendingAudio => _sink.HasPending;

    public bool SentencesEmpty => Segmenter.IsEmpty && _items.All(i => i.Status is SpeechStatus.Played or SpeechStatus.Failed);

    /// <summary>Feeds assistant text and returns the sentences that became speakable.</summary>
    public IReadOnlyList<SpeechItem> OnText(string text, long sessionEventSeq)
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

        var ready = Segmenter.Push(cleaned);
        var items = new List<SpeechItem>();

        foreach (var sentence in ready)
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
    /// Ends the turn: flushes the trailing fragment, starts playback and closes the gate. Until this point
    /// everything has been synthesized but held — the assistant must not start speaking mid-sentence.
    /// </summary>
    public async Task FlushTurnAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return;
        }

        foreach (var sentence in Segmenter.Flush())
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

        _sink.Release();

        if (_settings.MuteDuringTts && _items.Any(i => i.Status is SpeechStatus.Played or SpeechStatus.Playing))
        {
            _gate.Engage(_sink.DrainMs);
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
            var queued = _items.FirstOrDefault(i => i.Status == SpeechStatus.Queued);
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
                var emitted = false;
                await foreach (var chunk in _tts.SynthesizeAsync(item.Text, ct).ConfigureAwait(false))
                {
                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    emitted = true;
                    _sink.Enqueue(chunk, _settings.SampleRate);
                }

                // A final marker is mandatory: without it the player waits for this sequence forever.
                _sink.Enqueue(Array.Empty<float>(), _settings.SampleRate, final: true);

                Replace(item with { Status = emitted ? SpeechStatus.Played : SpeechStatus.Played });
                played++;
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
                _log($"voice: synthesis failed for '{item.Text}': {ex.Message}");
            }
            finally
            {
                TtsBusy = false;
            }
        }

        return played;
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
}
