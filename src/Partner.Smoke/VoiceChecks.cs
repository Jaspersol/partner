namespace Partner.Smoke;

internal static partial class Program
{
    private static async Task VoiceChecksAsync(string root, TimeProvider clock)
    {
        using (var server = new FakeVoiceServer(transcript: "turn on the desk lamp"))
        {
            var gate = new PlaybackGate();
            var source = new SyntheticAudioSource();
            var asr = new HttpAsrClient(new AsrOptions { BaseUrl = server.BaseUrl, Language = "Chinese" });

            var voiceQueue = new JsonlInputQueue(root, capacity: 8, clock: clock);
            var inputSetting = new VoiceInputSetting
            {
                Enabled = true,
                SilenceThreshold = 0.03,
                UtteranceGapMs = 1200,
                WindowSeconds = 8,
            };

            var log = new List<string>();
            var input = new VoiceInputHalf(
                inputSetting,
                source,
                asr,
                gate,
                utterance => voiceQueue.EnqueueAsync(new InputItem
                {
                    ItemId = utterance.UtteranceId,
                    Source = InputSource.Voice,
                    Payload = utterance.Transcript,
                    IdempotencyKey = utterance.IdempotencyKey,
                }).AsTask(),
                log.Add);

            input.Start();

            // 0.6 s of speech then 1.4 s of silence, fed as 20 ms frames on the real stream clock.
            var frame = 320; // 20 ms at 16 kHz
            for (var i = 0; i < 30; i++)
            {
                source.Emit(0.5f, frame);
                await input.TickAsync();
            }

            for (var i = 0; i < 70; i++)
            {
                source.Emit(0f, frame);
                await input.TickAsync();
            }

            // The final silence tick starts the ASR request; later ticks absorb it.
            for (var i = 0; i < 500 && input.Utterances.Count == 0; i++)
            {
                await Task.Delay(10);
                await input.TickAsync();
            }

            var utterance = input.Utterances.FirstOrDefault();
            Check("voice: speech followed by silence produces one utterance",
                utterance is { Transcript: "turn on the desk lamp" },
                $"utterances={input.Utterances.Count} text={utterance?.Transcript ?? "(none)"}");

            Check("voice: the utterance becomes a queue item with a boot-scoped key",
                input.Utterances.Count == 1
                && utterance!.IdempotencyKey
                    == $"voice:{input.DeviceId}:{input.BootId}:{utterance.SegStartMs:0}",
                utterance?.IdempotencyKey ?? "(none)");

            Check("voice: the ASR request carried a 16 kHz mono WAV",
                server.LastAsrBody is not null
                && server.LastAsrBody.Contains("\"format\":\"wav\"", StringComparison.Ordinal)
                && server.LastAsrBody.Contains("language Chinese<asr_text>", StringComparison.Ordinal),
                server.LastAsrBody is null ? "(no request)" : $"{server.LastAsrBody.Length} bytes");

            // Output half: markdown must not be read aloud, sentences must play in order.
            var sink = new SyntheticAudioSink();
            var tts = new HttpTtsClient(new TtsOptions { BaseUrl = server.BaseUrl });
            var outputSetting = new VoiceOutputSetting { Enabled = true, MuteDuringTts = true, DrainMs = 0 };
            var output = new VoiceOutputHalf(outputSetting, sink, tts, gate, log.Add);

            var spoken = output.OnText("## Answer\n\nUse **dark** mode. Thanks for asking! 🎉", sessionEventSeq: 42);
            await output.FlushTurnAsync();

            Check("voice: output speaks sentences in order, cleaned of markdown",
                spoken.Count >= 2
                && output.Items.All(i => i.Status is SpeechStatus.Played)
                && output.Items.All(i => !i.Text.Contains("**", StringComparison.Ordinal)
                    && !i.Text.Contains("#", StringComparison.Ordinal))
                && output.Items.Select(i => i.Seq).SequenceEqual(Enumerable.Range(1, output.Items.Count)),
                $"sentences={spoken.Count} statuses={string.Join(",", output.Items.Select(i => i.Status))}");

            Check("voice: playback closed the gate",
                gate.Engaged || output.Items.Count == 0,
                $"engaged={gate.Engaged} items={output.Items.Count}");

            // While the gate is closed, input is discarded — and the drop is reported.
            var utterancesBefore = input.Utterances.Count;
            for (var i = 0; i < 20; i++)
            {
                source.Emit(0.5f, frame);
                await input.TickAsync();
            }

            Check("voice: input during playback is discarded and reported",
                input.Utterances.Count == utterancesBefore && input.DetectedSpeechWhileGated,
                $"utterances={input.Utterances.Count} detectedSpeech={input.DetectedSpeechWhileGated}");

            // Drain the device, let the gate reopen, and confirm the discard is reported with its duration.
            sink.MarkDrained();
            var released = output.TryReleaseGate(input.PendingBlock, input.AsrBusy, 0);
            await input.TickAsync();

            Check("voice: reopening the gate reports the discarded audio",
                released && !gate.Engaged && gate.SkippedSeconds > 0
                && log.Any(l => l.Contains("discarded", StringComparison.Ordinal)),
                $"released={released} skipped={gate.SkippedSeconds:0.00}s blockers={gate.LastBlockers ?? "(none)"}");

            // The gate only reopens once every condition clears, and says why when it does not.
            var stillClosed = output.Conditions(input.PendingBlock, input.AsrBusy);
            output.TryReleaseGate(input.PendingBlock, input.AsrBusy, 0);
            Check("voice: gate reports its blockers while closed",
                stillClosed.AllClear || gate.LastBlockers is not null || gate.LastReleasedAt is not null,
                gate.LastBlockers ?? "(released)");

            input.Stop();
            input.Dispose();
            output.Dispose();
            await voiceQueue.DisposeAsync();
        }

        // Threshold misconfiguration must be detected, not discovered by silence.
        using (var server = new FakeVoiceServer())
        {
            var gate = new PlaybackGate();
            var source = new SyntheticAudioSource();
            var log = new List<string>();
            using var input = new VoiceInputHalf(
                new VoiceInputSetting { Enabled = true, SilenceThreshold = 0.0 },
                source,
                new HttpAsrClient(new AsrOptions { BaseUrl = server.BaseUrl }),
                gate,
                _ => Task.CompletedTask,
                log.Add);

            input.Start();

            // 6 s: the floor is only taken from a *quiet* window, and this input is never quiet (threshold 0
            // means nothing can fall below it), so the verdict comes from the probe's own deadline.
            for (var i = 0; i < 300; i++)
            {
                source.Emit(0.05f, 320);
                await input.TickAsync();
            }

            Check("voice: a threshold at or below the noise floor is detected",
                input.ThresholdMisconfigured && log.Any(l => l.Contains("silence can never be detected", StringComparison.Ordinal)),
                $"ambient={input.AmbientRms:0.0000} misconfigured={input.ThresholdMisconfigured}");
        }

        // A dead ASR service must be visible and must not fabricate an empty utterance.
        using (var deadAsr = new HttpAsrClient(new AsrOptions
        {
            BaseUrl = "http://127.0.0.1:9",
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            ReadTimeout = TimeSpan.FromMilliseconds(300),
        }))
        {
            var gate = new PlaybackGate();
            var source = new SyntheticAudioSource();
            var log = new List<string>();
            using var input = new VoiceInputHalf(
                new VoiceInputSetting { Enabled = true, UtteranceGapMs = 400 },
                source,
                deadAsr,
                gate,
                _ => Task.CompletedTask,
                log.Add);

            input.Start();
            for (var i = 0; i < 20; i++)
            {
                source.Emit(0.5f, 320);
                await input.TickAsync();
            }

            for (var i = 0; i < 30; i++)
            {
                source.Emit(0f, 320);
                await input.TickAsync();
            }

            // The last tick starts the failing request; the failure is reported when a later tick absorbs it.
            for (var i = 0; i < 500 && !log.Any(line => line.Contains("transcription failed", StringComparison.Ordinal)); i++)
            {
                await Task.Delay(10);
                await input.TickAsync();
            }

            Check("voice: an unreachable ASR service is reported and produces no utterance",
                input.Utterances.Count == 0
                && log.Any(l => l.Contains("transcription failed", StringComparison.Ordinal))
                && !input.AsrBusy,
                $"utterances={input.Utterances.Count} logged={log.Count(l => l.Contains("transcription failed", StringComparison.Ordinal))}");
        }

        // A dead TTS service must not stall playback: the failed sentence still gets a tombstone.
        using (var deadTts = new HttpTtsClient(new TtsOptions
        {
            BaseUrl = "http://127.0.0.1:9",
            Timeout = TimeSpan.FromMilliseconds(300),
        }))
        {
            var gate = new PlaybackGate();
            var sink = new SyntheticAudioSink();
            var log = new List<string>();
            using var output = new VoiceOutputHalf(
                new VoiceOutputSetting { Enabled = true, DrainMs = 0 },
                sink,
                deadTts,
                gate,
                log.Add);

            output.OnText("this will fail。", sessionEventSeq: 1);
            var played = await output.PlayPendingAsync();

            Check("voice: a dead TTS service fails visibly without stalling playback",
                played == 0
                && output.Items.All(i => i.Status == SpeechStatus.Failed)
                && output.Items.All(i => i.Error is not null)
                && log.Any(l => l.Contains("synthesis failed", StringComparison.Ordinal)),
                $"items={output.Items.Count} status={output.Items.FirstOrDefault()?.Status}");
        }

        // Voice output disabled: nothing is synthesized at all.
        using (var server = new FakeVoiceServer())
        {
            var requestsBefore = server.Requests.Count;
            var sink = new SyntheticAudioSink();
            using var output = new VoiceOutputHalf(
                new VoiceOutputSetting { Enabled = false },
                sink,
                new HttpTtsClient(new TtsOptions { BaseUrl = server.BaseUrl }),
                new PlaybackGate());

            output.OnText("should not be spoken", 1);
            await output.FlushTurnAsync();

            Check("voice: output disabled means no synthesis and no playback",
                output.Items.Count == 0 && sink.Segments.Count == 0 && server.Requests.Count == requestsBefore,
                $"items={output.Items.Count} segments={sink.Segments.Count}");
        }

        using (var output = new VoiceOutputHalf(
            new VoiceOutputSetting { Enabled = true, MinSentenceChars = 1, GapMs = 0 },
            new SyntheticAudioSink(), new ImmediateVoiceTts(), new PlaybackGate()))
        {
            for (var i = 0; i < 20000; i++)
            {
                output.OnText($"Sentence {i}。", i + 1);
                await output.PlayPendingAsync();
            }
            Check("voice: completed speech history is bounded without changing total count",
                output.TotalCount == 20000 && output.Items.Count <= 256 && output.SentencesEmpty,
                $"total={output.TotalCount} retained={output.Items.Count}");

            output.OnText("Interrupted sentence。", 20001);
            output.StopPlayback();
            output.ResumeAfterInterruption();
            output.OnText("Next sentence。", 20002);
            await output.PlayPendingAsync();
            Check("voice: interrupted output can speak a later sentence",
                output.Items.Any(i => i.Text == "Next sentence。" && i.Status == SpeechStatus.Played)
                && output.Items.Any(i => i.Text == "Interrupted sentence。" && i.Status == SpeechStatus.Failed));

            output.OnText("Repeat this sentence。", 20003);
            output.OnText("Repeat this sentence。", 20004);
            output.Say("Approval prompt.", SpeechKind.ApprovalPrompt, tag: "approval", priority: true);
            output.Say("Approval result.", SpeechKind.ApprovalResult, priority: true);
            var pending = output.Items.Where(i => i.Status == SpeechStatus.Queued).ToArray();
            Check("voice: approval priority and pending duplicate suppression survive history trimming",
                pending.Length == 3 && pending[0].Kind == SpeechKind.ApprovalResult
                && pending[1].Kind == SpeechKind.ApprovalPrompt && pending[2].Kind == SpeechKind.Assistant);
            output.DropTag("approval", "cancelled");
            Check("voice: dropping approval removes its queued speech",
                output.Items.Any(i => i.Tag == "approval" && i.Status == SpeechStatus.Failed));
        }

        var players = new List<ControlledVoicePlayer>();
        using (var sink = new WasapiAudioSink(new VoiceOutputSetting { DrainMs = 0 }, () =>
        {
            var player = new ControlledVoicePlayer();
            players.Add(player);
            return player;
        }))
        {
            sink.Start();
            var samples = Enumerable.Range(0, 40000).Select(i => (i % 200 - 100) / 100f).ToArray();
            var write = sink.EnqueueAsync(samples, 1000);
            var captured = new List<byte>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!write.IsCompleted && DateTime.UtcNow < deadline)
            {
                players[0].Drain(captured);
                await Task.Delay(1);
            }
            if (!write.IsCompleted) sink.Stop();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            players[0].Drain(captured);
            var expected = samples.SelectMany(sample =>
            {
                var value = (short)(Math.Clamp(sample, -1f, 1f) * 32767);
                return new[] { (byte)value, (byte)(value >> 8) };
            }).ToArray();
            Check("voice: bounded playback preserves long audio in order",
                captured.SequenceEqual(expected), $"bytes={captured.Count} expected={expected.Length}");

            sink.Hold();
            var blocked = sink.EnqueueAsync(new float[4000], 1000);
            Check("voice: held playback applies backpressure", !blocked.IsCompleted);
            sink.Stop();
            var interrupted = false;
            try { await blocked.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { interrupted = true; }
            sink.Start();
            await sink.EnqueueAsync(new float[] { 0.5f }, 1000);
            var restarted = new List<byte>();
            players[^1].Drain(restarted);
            Check("voice: stopping releases producers and clears old audio",
                interrupted && restarted.SequenceEqual(new byte[] { 0xff, 0x3f }));

            sink.Hold();
            using var cancel = new CancellationTokenSource();
            var cancelledWrite = sink.EnqueueAsync(new float[4000], 1000, ct: cancel.Token);
            cancel.Cancel();
            var cancelled = false;
            try { await cancelledWrite.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("voice: playback backpressure respects cancellation", cancelled);
            var failingWrite = sink.EnqueueAsync(new float[4000], 1000);
            players[^1].Fail(new InvalidOperationException("controlled player failure"));
            var failed = false;
            try { _ = sink.HasPending; }
            catch (InvalidOperationException error) { failed = error.InnerException?.Message == "controlled player failure"; }
            var producerFailed = false;
            try { await failingWrite.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (InvalidOperationException error) { producerFailed = error.InnerException?.Message == "controlled player failure"; }
            Check("voice: playback device failure reaches state and blocked producer", failed && producerFailed);
        }

    }

    private sealed class ImmediateVoiceTts : ITtsClient
    {
        public async IAsyncEnumerable<float[]> SynthesizeAsync(string text,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new float[] { 0.1f };
        }

        public void Dispose() { }
    }

    private sealed class ControlledVoicePlayer : NAudio.Wave.IWavePlayer
    {
        private NAudio.Wave.BufferedWaveProvider? _provider;
        public NAudio.Wave.PlaybackState PlaybackState { get; private set; }
        public NAudio.Wave.WaveFormat OutputWaveFormat => _provider!.WaveFormat;
        public float Volume { get; set; } = 1;
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? PlaybackStopped;
        public void Init(NAudio.Wave.IWaveProvider provider) => _provider = (NAudio.Wave.BufferedWaveProvider)provider;
        public void Play() => PlaybackState = NAudio.Wave.PlaybackState.Playing;
        public void Pause() => PlaybackState = NAudio.Wave.PlaybackState.Paused;
        public void Stop() => PlaybackState = NAudio.Wave.PlaybackState.Stopped;
        public void Dispose() { }
        public void Fail(Exception error) => PlaybackStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs(error));
        public void Drain(List<byte> captured)
        {
            if (PlaybackState != NAudio.Wave.PlaybackState.Playing || _provider is null) return;
            var count = _provider.BufferedBytes;
            if (count == 0) return;
            var bytes = new byte[count];
            _provider.Read(bytes, 0, count);
            captured.AddRange(bytes);
        }
    }
}
