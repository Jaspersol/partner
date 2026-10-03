using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Partner.Api.Auth;
using Partner.Api.Settings;
using Partner.Api.Voice;
using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Settings;
using Partner.Voice.Tts;

namespace Partner.Api.Tests;

/// <summary>
/// The voice host, driven with synthetic audio and scripted ASR/TTS. Real hardware would make these tests
/// depend on the machine's microphone, and the pause logic is the part worth pinning down anyway.
/// </summary>
public sealed class VoiceHostTests : ApiFixture
{
    private readonly SyntheticVoice _components = new();

    protected override string WorkspaceName => "partner-voice-tests";

    protected override IVoiceComponents Components => _components;

    protected override string ReadinessPath => "/api/voice";

    private Task<JsonElement> StateAsync() => _http.GetFromJsonAsync<JsonElement>("/api/voice");

    private async Task PutAsync(object settings)
    {
        var response = await _http.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>PUT replaces every section, so a test that edits one must send the rest as well.</summary>
    private static object EnabledVoice(
        string? asrBaseUrl = "http://127.0.0.1:45101",
        string? ttsBaseUrl = "http://127.0.0.1:45102",
        object? model = null) => new
    {
        input = new { enabled = true, utteranceGapMs = 200 },
        output = new { enabled = true },
        asr = new { baseUrl = asrBaseUrl, model = "test-asr" },
        tts = new { baseUrl = ttsBaseUrl, lang = "zh" },
        model = model ?? new { baseUrl = (string?)null, model = (string?)null },
    };

    [Fact]
    public async Task Both_halves_are_off_by_default_and_say_so()
    {
        var state = await StateAsync();

        Assert.False(state.GetProperty("input").GetProperty("running").GetBoolean());
        Assert.False(state.GetProperty("output").GetProperty("running").GetBoolean());
        Assert.Contains("未启用", state.GetProperty("input").GetProperty("reason").GetString()!, StringComparison.Ordinal);
        Assert.Contains("未启用", state.GetProperty("output").GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabling_input_without_an_asr_endpoint_explains_the_gap()
    {
        await PutAsync(new
        {
            input = new { enabled = true },
            asr = new { baseUrl = (string?)null },
        });

        var state = await StateAsync();
        var input = state.GetProperty("input");

        Assert.False(input.GetProperty("running").GetBoolean());
        Assert.Contains("ASR", input.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_will_not_open_is_reported_and_the_other_half_still_runs()
    {
        _components.FailToOpenSink = true;

        await PutAsync(EnabledVoice());

        var state = await StateAsync();

        // The input half is unaffected by the output device being unavailable.
        Assert.True(state.GetProperty("input").GetProperty("running").GetBoolean());
        Assert.Equal("synthetic-mic", state.GetProperty("input").GetProperty("device").GetString());

        var output = state.GetProperty("output");
        Assert.False(output.GetProperty("running").GetBoolean());
        Assert.Contains("打开输出设备失败", output.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_utterance_becomes_a_queue_item_with_the_boot_scoped_key()
    {
        _components.AsrTranscript = "打开书房的灯";
        await PutAsync(EnabledVoice());

        var source = _components.LastSource!;

        // Fed as 20 ms frames on a stream-like cadence: the pipeline expects to be ticked as audio arrives.
        for (var i = 0; i < 30; i++)
        {
            source.Emit(0.5f, 320);
            await Task.Delay(25);
        }

        for (var i = 0; i < 70; i++)
        {
            source.Emit(0f, 320);
            await Task.Delay(25);
        }

        var queued = await Eventually.IsTrueAsync(async () =>
        {
            var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
            return queue.GetProperty("items").EnumerateArray().Any(item =>
                item.GetProperty("source").GetString() == "Voice"
                && item.GetProperty("payload").GetString() == "打开书房的灯");
        });

        var voiceState = await StateAsync();
        Assert.True(queued, $"the utterance never reached the queue; input={voiceState.GetProperty("input")}");

        // The key is what makes a retry safe, and the queue view does not carry it — read the log instead.
        var items = new List<Core.Queue.InputItem>();
        await foreach (var entry in _context.Queue.ReadLogAsync())
        {
            if (entry.Source == Core.Contracts.InputSource.Voice)
            {
                items.Add(entry);
            }
        }

        var item = Assert.Single(items);
        Assert.Equal("打开书房的灯", item.Payload);
        Assert.Equal("Queued", item.Status.ToString());
        Assert.StartsWith("voice:", item.IdempotencyKey, StringComparison.Ordinal);

        // device:boot:startMs — the boot id is in there because the stream clock resets on restart.
        Assert.Equal(3, item.IdempotencyKey.Split(':').Length - 1);
    }

    [Fact]
    public async Task Assistant_text_is_synthesised_and_played()
    {
        await PutAsync(EnabledVoice());
        await _context.Session.AppendAsync(new Core.Sessions.SessionEvent
        {
            Kind = Core.Contracts.SessionEventKind.Assistant,
            Source = Core.Contracts.InputSource.Text,
            Body = "书房的灯已经打开了。窗帘也拉上了。",
        });

        var played = await Eventually.IsTrueAsync(async () =>
        {
            var state = await StateAsync();
            return state.GetProperty("output").GetProperty("count").GetInt32() >= 2;
        });

        Assert.True(played, "the assistant text was never spoken");

        // Two sentences, synthesised in order, and handed to the sink as PCM.
        Assert.Equal(2, _components.TtsRequests.Count);
        Assert.NotEmpty(_components.LastSink!.Segments);
    }

    [Fact]
    public async Task Changing_the_voice_section_restarts_only_the_voice_host()
    {
        await PutAsync(EnabledVoice());
        var before = await StateAsync();
        Assert.True(before.GetProperty("input").GetProperty("running").GetBoolean());

        // Editing the model section must not reopen the microphone.
        var openedSources = _components.SourcesOpened;
        await PutAsync(EnabledVoice(model: new { baseUrl = "http://127.0.0.1:9/v1", model = "m" }));

        var after = await StateAsync();
        Assert.True(after.GetProperty("input").GetProperty("running").GetBoolean());
        Assert.Equal(openedSources, _components.SourcesOpened);
    }
}

/// <summary>Synthetic devices and scripted services: the whole pipeline without hardware or a network.</summary>
internal sealed class SyntheticVoice : IVoiceComponents
{
    public SyntheticAudioSource? LastSource { get; private set; }

    public SyntheticAudioSink? LastSink { get; private set; }

    public List<string> TtsRequests { get; } = new();

    public string AsrTranscript { get; set; } = "hello";

    public bool FailToOpenSink { get; set; }

    public int SourcesOpened { get; private set; }

    public IAudioSource OpenSource(VoiceInputSetting setting)
    {
        SourcesOpened++;
        // The capture rate is fixed by the ASR contract, not by a setting.
        LastSource = new SyntheticAudioSource(16_000, "synthetic-mic");
        return LastSource;
    }

    public IAudioSink OpenSink(VoiceOutputSetting setting)
    {
        if (FailToOpenSink)
        {
            throw new InvalidOperationException("no output device");
        }

        LastSink = new SyntheticAudioSink(setting.SampleRate, "synthetic-speaker");
        return LastSink;
    }

    public IReadOnlyList<string> ListInputDevices() => new[] { "synthetic-mic", "synthetic-mic-2" };

    public IReadOnlyList<string> ListOutputDevices() => new[] { "synthetic-speaker" };

    public IAsrClient CreateAsr(AsrOptions options) => new ScriptedAsr(this);

    public ITtsClient CreateTts(TtsOptions options) => new ScriptedTts(this);

    private sealed class ScriptedAsr(SyntheticVoice owner) : IAsrClient
    {
        public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default) =>
            Task.FromResult(owner.AsrTranscript);

        public async IAsyncEnumerable<string> TranscribeStreamAsync(
            float[] pcm,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return owner.AsrTranscript;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedTts(SyntheticVoice owner) : ITtsClient
    {
        public async IAsyncEnumerable<float[]> SynthesizeAsync(
            string text,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            owner.TtsRequests.Add(text);
            yield return new float[2400];
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }
}
