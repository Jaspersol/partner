using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice.Tests;

/// <summary>
/// A transcription takes seconds, and the tick used to wait for it: end-of-speech detection and the wake probe
/// stood still for the whole request. Now the request runs beside the ticks and its result is taken on the next one.
/// </summary>
public sealed class AsrInFlightTests
{
    private const int SampleRate = 16_000;
    private const int Frame = 320; // 20 ms

    private sealed class GatedAsr : IAsrClient
    {
        private readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests { get; private set; }

        public void Answer(string text) => _answer.TrySetResult(text);

        public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default)
        {
            Requests++;
            return _answer.Task;
        }

        public async IAsyncEnumerable<string> TranscribeStreamAsync(
            float[] pcm,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await _answer.Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    private static async Task FeedAsync(VoiceInputHalf half, SyntheticAudioSource source, double seconds, float amplitude)
    {
        for (var i = 0; i < seconds * SampleRate / Frame; i++)
        {
            source.Emit(amplitude, Frame);
            await half.TickAsync();
        }
    }

    [Fact]
    public async Task Ticks_keep_running_while_a_transcription_is_in_flight_and_the_result_is_taken_afterwards()
    {
        var source = new SyntheticAudioSource(SampleRate);
        var asr = new GatedAsr();
        var heard = new List<string>();
        var half = new VoiceInputHalf(
            new VoiceInputSetting { Enabled = true, SilenceThreshold = 0.03, UtteranceGapMs = 800, WindowSeconds = 8 },
            source,
            asr,
            new PlaybackGate(),
            utterance =>
            {
                heard.Add(utterance.Transcript);
                return Task.CompletedTask;
            });
        half.Start();

        await FeedAsync(half, source, 0.6, 0f);  // let the noise-floor probe settle
        await FeedAsync(half, source, 2.0, 0.5f); // speech
        await FeedAsync(half, source, 1.5, 0f);   // the gap that ends it: the block is sent

        Assert.Equal(1, asr.Requests);
        Assert.True(half.AsrBusy);
        Assert.Empty(heard);

        // The request is still out. Ticks go on (this loop would have hung on the first one before), a second
        // sentence is spoken meanwhile, and nothing is sent twice while the first is unanswered.
        await FeedAsync(half, source, 1.5, 0.5f);
        await FeedAsync(half, source, 1.5, 0f);
        Assert.Equal(1, asr.Requests);
        Assert.True(half.AsrBusy);

        asr.Answer("打开书房的灯");
        await FeedAsync(half, source, 0.2, 0f);

        Assert.Contains("打开书房的灯", heard);
    }

    private sealed class SilentAsr : IAsrClient
    {
        public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public async IAsyncEnumerable<string> TranscribeStreamAsync(
            float[] pcm,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }

    private static async Task<int> BargeInsAsync(bool enabled, float speakingAmplitude)
    {
        var source = new SyntheticAudioSource(SampleRate);
        var gate = new PlaybackGate();
        var interruptions = 0;
        var half = new VoiceInputHalf(
            new VoiceInputSetting { Enabled = true, SilenceThreshold = 0.03, WindowSeconds = 8, BargeIn = enabled, BargeInMs = 600 },
            source,
            new SilentAsr(),
            gate,
            _ => Task.CompletedTask,
            onBargeIn: () => interruptions++);
        half.Start();

        await FeedAsync(half, source, 0.6, 0f);
        gate.Engage(); // the assistant is speaking
        await FeedAsync(half, source, 2.0, speakingAmplitude);
        return interruptions;
    }

    [Fact]
    public async Task Sustained_loud_sound_while_the_assistant_speaks_interrupts_it_once()
    {
        Assert.Equal(1, await BargeInsAsync(enabled: true, speakingAmplitude: 0.5f));
    }

    [Fact]
    public async Task Sound_barely_above_the_threshold_is_taken_for_the_assistants_own_voice()
    {
        Assert.Equal(0, await BargeInsAsync(enabled: true, speakingAmplitude: 0.04f));
    }

    [Fact]
    public async Task Barge_in_is_off_unless_asked_for()
    {
        Assert.Equal(0, await BargeInsAsync(enabled: false, speakingAmplitude: 0.5f));
    }
}
