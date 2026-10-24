using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Partner.Api.Settings;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;
using Partner.Voice.Asr;
using Partner.Voice.Tts;

namespace Partner.Api.Endpoints;

/// <summary>
/// Settings page connectivity tests. One endpoint per service kind, each making the smallest real round-trip
/// that proves the configured address speaks the configured protocol. ASR transcribes a TTS-generated sample (or
/// plain silence when no voice is configured), TTS returns its audio for the UI to play, and the model test
/// proves both text and vision with one-token completions.
/// </summary>
public static class VoiceTestEndpoints
{
    /// <summary>AsrResult/TtsResult add the payload the settings cards render or play.</summary>
    public sealed record VoiceTestResult(bool Ok, string Detail, int LatencyMs);

    /// <summary>Transcribed is the engine's raw output, shown in the UI immediately.</summary>
    public sealed record AsrTestResult(bool Ok, string Detail, int LatencyMs, string Transcribed);

    public sealed record TtsTestResult(bool Ok, string Detail, int LatencyMs, string? AudioBase64, double Seconds);

    public static void Map(WebApplication app, ApiContext ctx)
    {
        app.MapPost("/api/voice/test/asr", async (LocalEngineHost engines, CancellationToken ct) =>
        {
            var result = await TestAsrAsync(ctx, ct).ConfigureAwait(false);
            return Results.Ok(result.Ok ? result : result with { Detail = result.Detail + EngineHint(engines.State.Kws, ctx.Settings.Load().Local.Kws, ctx.Settings.Load().Asr.BaseUrl) });
        });

        app.MapPost("/api/voice/test/tts", async (LocalEngineHost engines, CancellationToken ct) =>
        {
            var result = await TestTtsAsync(ctx, ct).ConfigureAwait(false);
            return Results.Ok(result.Ok ? result : result with { Detail = result.Detail + EngineHint(engines.State.Tts, ctx.Settings.Load().Local.Tts, ctx.Settings.Load().Tts.BaseUrl) });
        });

        app.MapPost("/api/voice/test/kws", async (LocalEngineHost engines, CancellationToken ct) =>
        {
            var result = await TestKwsAsync(ctx, ct).ConfigureAwait(false);
            return Results.Ok(result.Ok ? result : result with { Detail = result.Detail + EngineHint(engines.State.Kws, ctx.Settings.Load().Local.Kws, ctx.Settings.Load().Asr.BaseUrl) });
        });

        app.MapPost("/api/voice/test/model", async (CancellationToken ct) =>
            Results.Ok(await TestModelAsync(ctx, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// Adds the local engine's state when a test fails: a connection failure usually means the engine never
    /// started, and the reason is in the line Partner logged when it spawned it. Reported even when the switch is
    /// off, or the user will blame the address.
    /// </summary>
    private static string EngineHint(LocalEngineState state, LocalEngineSettings settings, string? baseUrl)
    {
        if (state.Running)
        {
            return string.Empty;
        }

        if (settings.Enabled)
        {
            return $"；本地引擎没在运行：{state.Reason}";
        }

        // Switch off: only worth saying when the client points at the port that engine would have listened on.
        var local = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == settings.Port;
        return local ? "；本地引擎的「由 Partner 启动」没开" : string.Empty;
    }

    /// <summary>
    /// ASR live test: listens on the configured input device for 2 s and feeds the audio to the engine, returning
    /// its transcript. If the device cannot open, falls back to a silence probe (connectivity only).
    /// </summary>
    private static async Task<AsrTestResult> TestAsrAsync(ApiContext ctx, CancellationToken ct)
    {
        var settings = ctx.Settings.Load();
        var options = VoiceSettingsMapping.ToAsr(settings.Asr);
        if (options is null)
        {
            return new AsrTestResult(false, "未配置 ASR 服务地址", 0, "");
        }

        const double listenSeconds = 2.0;
        float[] audio;
        var note = "实听 2 s";
        try
        {
            audio = RecordFromInputAsync(settings.Input.DeviceId, listenSeconds, options.SampleRate, ct);
        }
        catch (Exception error)
        {
            audio = new float[options.SampleRate / 2];
            note = $"输入设备不可用（{error.Message}），用 0.5 s 静音探测";
        }

        using var client = WasapiVoiceComponents.CreateAsrClient(options);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var text = await client.TranscribeAsync(audio, ct).ConfigureAwait(false);
            return JudgeAsr(audio, settings.Input.SilenceThreshold, text, note, (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new AsrTestResult(false, $"不可达：{error.Message}", (int)watch.ElapsedMilliseconds, "");
        }
    }

    /// <summary>TTS live test: synthesizes a full sentence and returns base64 WAV for the UI to play.</summary>
    private static async Task<TtsTestResult> TestTtsAsync(ApiContext ctx, CancellationToken ct)
    {
        var tts = ctx.Settings.Load().Tts;
        var options = VoiceSettingsMapping.ToTts(tts);
        if (options is null)
        {
            return new TtsTestResult(false, "未配置 TTS 服务地址", 0, null, 0);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var pcm = await SynthesizeOnceAsync(options, "你好，我是 Partner，这是语音合成测试。", ct).ConfigureAwait(false);
            if (pcm.Length == 0)
            {
                return new TtsTestResult(false, "可达但返回空音频", (int)watch.ElapsedMilliseconds, null, 0);
            }

            var seconds = pcm.Length / (double)options.SampleRate;
            var wav = PcmToWav16(pcm, options.SampleRate);
            return new TtsTestResult(
                true,
                $"可达，合成 {seconds:0.0}s 音频，点击播放可试听",
                (int)watch.ElapsedMilliseconds,
                Convert.ToBase64String(wav),
                seconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new TtsTestResult(false, $"不可达：{error.Message}", (int)watch.ElapsedMilliseconds, null, 0);
        }
    }

    /// <summary>Listens on the given input device for <c>seconds</c>, collecting float32 PCM. The input half shares
    /// the same microphone and the host guarantees it is stopped first (Reconfigure gates it), so recording here
    /// is safe.</summary>
    private static float[] RecordFromInputAsync(string? deviceId, double seconds, int sampleRate, CancellationToken ct)
    {
        var collected = new List<float[]>();
        var target = (int)(seconds * sampleRate);
        var received = 0;
        using var wait = new SemaphoreSlim(0, 1);
        Partner.Voice.Audio.WasapiAudioSource source;
        try
        {
            source = new Partner.Voice.Audio.WasapiAudioSource(new Partner.Voice.Settings.VoiceInputSetting
            {
                DeviceId = deviceId,
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"打开输入设备失败：{error.Message}", error);
        }

        try
        {
            source.Start(samples =>
            {
                lock (collected)
                {
                    collected.Add(samples);
                    received += samples.Length;
                }

                if (received >= target)
                {
                    wait.Release();
                }
            });

            try
            {
                wait.Wait(TimeSpan.FromSeconds(seconds + 2), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
        finally
        {
            source.Stop();
            source.Dispose();
        }

        lock (collected)
        {
            var merged = new float[Math.Min(received, target)];
            var offset = 0;
            foreach (var chunk in collected)
            {
                var take = Math.Min(chunk.Length, merged.Length - offset);
                Array.Copy(chunk, 0, merged, offset, take);
                offset += take;
                if (offset >= merged.Length)
                {
                    break;
                }
            }

            return merged;
        }
    }

    /// <summary>
    /// Judges an ASR live test. Extracted as a pure function because it carries the cure for "false green" in the
    /// settings page: if the whole clip is below the threshold, the microphone delivered nothing and the engine
    /// being reachable does not help — the pipeline can never produce a transcript, so this must be red, or
    /// "unplugged mic", "quiet room" and "threshold set too high" all look green.
    /// </summary>
    internal static AsrTestResult JudgeAsr(
        float[] audio,
        double silenceThreshold,
        string text,
        string note,
        int latencyMs)
    {
        var cleaned = text.Trim();
        var rms = SpeechLevel(audio);

        if (rms < silenceThreshold)
        {
            // Signal below the threshold but transcribed by the engine differs from no signal at all: the first is
            // a volume/threshold misconfiguration, the second is a device delivering nothing. Both are red, but
            // the wording must differ.
            var noSignal = rms < NoSignalFloor;
            var detail = noSignal
                ? $"没听到声音（RMS {rms:0.0000} 低于阈值 {silenceThreshold:0.0000}）：设备无信号，或测试时没说话（{note}）"
                : $"声音太小（RMS {rms:0.0000} 低于阈值 {silenceThreshold:0.0000}）：调高麦克风音量，或调低 silenceThreshold（{note}）";
            return new AsrTestResult(
                false,
                detail + (cleaned.Length > 0 ? $"；引擎却转出「{cleaned}」" : ""),
                latencyMs,
                cleaned);
        }

        var reply = cleaned.Length > 0
            ? $"识别到：「{cleaned}」（RMS {rms:0.0000}，{note}）"
            : $"可达但没识别出内容（有声音，RMS {rms:0.0000}；{note}）";
        return new AsrTestResult(true, reply, latencyMs, cleaned);
    }

    /// <summary>
    /// RMS of the loudest frame (50 ms window). A whole-clip average is diluted by silence around the sentence:
    /// 0.15 s of speech in 2 s drops the average to about 1/3.6 of the sentence, flagging normal loudness as "no
    /// sound". Peak matches the sentence detector, which uses the same 50 ms-window RMS against the threshold.
    /// </summary>
    private static double SpeechLevel(float[] pcm)
    {
        if (pcm.Length == 0)
        {
            return 0;
        }

        const int frame = 800; // 50 ms at 16 kHz (this path is always 16 kHz)
        var peak = 0.0;
        for (var i = 0; i < pcm.Length; i += frame)
        {
            var count = Math.Min(frame, pcm.Length - i);
            double sum = 0;
            for (var j = 0; j < count; j++)
            {
                var s = pcm[i + j];
                sum += (double)s * s;
            }

            var rms = Math.Sqrt(sum / count);
            if (rms > peak)
            {
                peak = rms;
            }
        }

        return peak;
    }

    /// <summary>Below this, the device delivered no signal at all (pure silence / 1 LSB jitter); above it, "too quiet".</summary>
    private const double NoSignalFloor = 0.001;

    private static async Task<float[]> SynthesizeOnceAsync(TtsOptions options, string text, CancellationToken ct)
    {
        using var client = WasapiVoiceComponents.CreateTtsClient(options);
        var collected = new List<float[]>();
        var total = 0;
        await foreach (var chunk in client.SynthesizeAsync(text, ct).ConfigureAwait(false))
        {
            collected.Add(chunk);
            total += chunk.Length;
        }

        var merged = new float[total];
        var offset = 0;
        foreach (var chunk in collected)
        {
            Array.Copy(chunk, 0, merged, offset, chunk.Length);
            offset += chunk.Length;
        }

        return merged;
    }

    /// <summary>16-bit mono WAV (Partner.Voice's PcmWav is internal, so this builds the same format).</summary>
    private static byte[] PcmToWav16(float[] pcm, int sampleRate)
    {
        var bytes = new byte[44 + pcm.Length * 2];
        var fs = pcm.Length * 2;
        WriteAscii(bytes, 0, "RIFF");
        WriteInt(bytes, 4, 36 + fs);
        WriteAscii(bytes, 8, "WAVE");
        WriteAscii(bytes, 12, "fmt ");
        WriteInt(bytes, 16, 16);
        bytes[20] = 1; // PCM
        bytes[22] = 1; // mono
        WriteInt(bytes, 24, sampleRate);
        WriteInt(bytes, 28, sampleRate * 2);
        bytes[32] = 2; // block align
        bytes[34] = 16; // bits
        WriteAscii(bytes, 36, "data");
        WriteInt(bytes, 40, fs);

        for (var i = 0; i < pcm.Length; i++)
        {
            var value = Math.Clamp(pcm[i], -1f, 1f) * 32767f;
            var encoded = (short)Math.Round(value);
            bytes[44 + i * 2] = (byte)encoded;
            bytes[45 + i * 2] = (byte)((uint)encoded >> 8);
        }

        return bytes;
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            target[offset + i] = (byte)text[i];
        }
    }

    private static void WriteInt(byte[] target, int offset, int value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// KWS: health + the live keyword list + one real probe. The probe feeds recorded audio into
    /// <c>/wake/infer</c>, because reading <c>/health</c> only proves the endpoint exists — a dictionary that
    /// lists the right words while the wake path never fires is exactly the state this test must separate.
    /// </summary>
    private static async Task<VoiceTestResult> TestKwsAsync(ApiContext ctx, CancellationToken ct)
    {
        var settings = ctx.Settings.Load();
        var base_ = settings.Asr.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_) || AsrProtocols.Normalize(settings.Asr.Protocol) != AsrProtocols.SenseVoice)
        {
            return new VoiceTestResult(false, "未配置 sensevoice 协议的 ASR 地址（KWS 与 ASR 同引擎）", 0);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var health = await http.GetAsync($"{base_}/health", ct).ConfigureAwait(false);
            var body = await health.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!health.IsSuccessStatusCode)
            {
                return new VoiceTestResult(false, $"HTTP {(int)health.StatusCode}", (int)watch.ElapsedMilliseconds);
            }

            var kws = body.Contains("\"kws\":true") || body.Contains("\"kws\": true");
            var keywords = Array.Empty<string>();
            try
            {
                using var response = await http.GetAsync($"{base_}/keywords", ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                keywords = document.RootElement.GetProperty("keywords")
                    .EnumerateArray().Select(node => node.GetString() ?? "").ToArray();
            }
            catch (Exception)
            {
                // /keywords is an extension of this repo's shell; a third-party sherpa service lacking it is not a failure.
            }

            var (probeOk, probeText) = await WakeProbeAsync(http, base_!, settings, ct).ConfigureAwait(false);
            var keywordText = keywords.Length > 0 ? $"：{string.Join("、", keywords)}" : "";
            var kwsText = kws ? "已启用" : "未启用";
            return new VoiceTestResult(
                probeOk,
                $"可达，唤醒词{kwsText}{keywordText}{probeText}",
                (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new VoiceTestResult(false, $"不可达：{error.Message}", (int)watch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Wake probe: records real audio and sends it to <c>/wake/infer</c>, returning (passed, detail). No signal
    /// counts as failure; signal heard but no hit counts as passed, because "did not speak during the test" and
    /// "wrong word list" look identical in the audio — report it honestly instead of guessing.
    /// </summary>
    private static async Task<(bool Ok, string Detail)> WakeProbeAsync(
        HttpClient http,
        string baseUrl,
        Settings.RuntimeSettings settings,
        CancellationToken ct)
    {
        // KWS accepts only 16 kHz mono float32: at any other rate the audio would play at the wrong speed, so skip.
        if (settings.Asr.SampleRate != 16_000)
        {
            return (true, $"；唤醒实测跳过（KWS 只吃 16 kHz，当前 ASR 采样率 {settings.Asr.SampleRate}）");
        }

        const double listenSeconds = 3.0;
        float[] audio;
        try
        {
            audio = RecordFromInputAsync(settings.Input.DeviceId, listenSeconds, 16_000, ct);
        }
        catch (Exception error)
        {
            return (false, $"；唤醒实测无法录音：{error.Message}");
        }

        var rms = SpeechLevel(audio);
        var floor = settings.Input.SilenceThreshold;
        if (rms < floor)
        {
            return (false, rms < NoSignalFloor
                ? $"；唤醒实测没听到声音（RMS {rms:0.0000} 低于阈值 {floor:0.0000}）：设备无信号"
                : $"；唤醒实测声音太小（RMS {rms:0.0000} 低于阈值 {floor:0.0000}）：调高麦克风音量或调低 silenceThreshold");
        }

        var bytes = new byte[audio.Length * sizeof(float)];
        Buffer.BlockCopy(audio, 0, bytes, 0, bytes.Length);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.PostAsync($"{baseUrl}/wake/infer", content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, $"；唤醒实测 HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;
        var hit = root.TryGetProperty("hit", out var hitElement) && hitElement.ValueKind == JsonValueKind.True;
        var keyword = root.TryGetProperty("keyword", out var keywordElement) && keywordElement.ValueKind == JsonValueKind.String
            ? keywordElement.GetString()
            : null;

        var heard = $"（录了 {listenSeconds:0} s，RMS {rms:0.0000}）";
        return hit
            ? (true, $"；唤醒实测命中「{keyword ?? "?"}」{heard}")
            : (true, $"；唤醒实测这 {listenSeconds:0} s 没命中唤醒词{heard}：测试时说了唤醒词吗？");
    }

    /// <summary>
    /// Text model: one 1-token chat completion proves key+url+model at once. Vision: a 1×1 red PNG offered the
    /// same way — models that accept it say 2xx; text-only endpoints usually 400. The flag is reported to the user
    /// rather than trusted for the desktop tool table (that stays SupportsImages).
    /// </summary>
    private static async Task<VoiceTestResult> TestModelAsync(ApiContext ctx, CancellationToken ct)
    {
        var settings = ctx.Settings.Load();
        var base_ = settings.Model.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_) || string.IsNullOrWhiteSpace(settings.Model.Model))
        {
            return new VoiceTestResult(false, "未配置 model.baseUrl 或 model.model", 0);
        }

        var anthropic = LlmProtocols.Normalize(settings.Model.Protocol) == LlmProtocols.Anthropic;
        if (anthropic)
        {
            // Same normalization the real client uses, so the probe tests the route a turn will take.
            base_ = AnthropicLlmClient.NormalizeBaseUrl(base_);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.Model.TimeoutSeconds > 0 ? settings.Model.TimeoutSeconds : 30) };
        if (!string.IsNullOrEmpty(settings.Model.ApiKey))
        {
            if (anthropic)
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", settings.Model.ApiKey);
            }
            else
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Model.ApiKey);
            }
        }

        if (anthropic)
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", AnthropicLlmClient.ApiVersion);
        }

        var chat = $"{base_}{(anthropic ? "/messages" : "/chat/completions")}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Vision probe first: it is the expensive request, so avoid two synthesized calls.
            var vision = await ChatAsync(http, chat, VisionBody(settings.Model.Model, anthropic), ct).ConfigureAwait(false);
            var visionOk = vision is not null;

            var text = await ChatAsync(http, chat, TextBody(settings.Model.Model, anthropic), ct).ConfigureAwait(false);
            if (text is null)
            {
                return new VoiceTestResult(false, "无响应", (int)watch.ElapsedMilliseconds);
            }

            var visionText = visionOk ? "支持" : "不支持";
            return new VoiceTestResult(
                true,
                $"可达，视觉{visionText}（设置里声明 {settings.Model.SupportsImages}）",
                (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new VoiceTestResult(false, $"不可达：{error.Message}", (int)watch.ElapsedMilliseconds);
        }
    }

    private static async Task<JsonElement?> ChatAsync(HttpClient http, string url, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return document.RootElement;
    }

    private static string TextBody(string model, bool anthropic) =>
        anthropic
            ? "{\"model\":\"" + model + "\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}"
            : "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":1}";

    // 1×1 red PNG as a data URI: enough to prove the endpoint accepts multimodal messages, small enough to be free.
    private static string VisionBody(string model, bool anthropic) =>
        anthropic
            ? "{\"model\":\"" + model + "\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":[" +
              "{\"type\":\"text\",\"text\":\"ping\"}," +
              "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==\"}}" +
              "]}]}"
            : "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":[" +
              "{\"type\":\"text\",\"text\":\"ping\"}," +
              "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==\"}}" +
              "]}],\"max_tokens\":1}";
}
