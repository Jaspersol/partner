using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Partner.Api.Settings;
using Partner.Api.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Tts;

namespace Partner.Api.Endpoints;

/// <summary>
/// Settings page's connectivity tests. One endpoint per service kind, each making the smallest real
/// round-trip that proves the configured address speaks the configured protocol — a settings row showing
/// "saved" proves nothing about reachability, so the UI runs these instead of hoping. ASR actually
/// transcribes a TTS-generated sample (or plain silence when no voice is configured), TTS returns its
/// audio for the UI to play, and the model test proves both text and vision with one-token completions.
/// </summary>
public static class VoiceTestEndpoints
{
    /// <summary>AsrResult/TtsResult add the payload the settings cards render or play.</summary>
    public sealed record VoiceTestResult(bool Ok, string Detail, int LatencyMs);

    /// <summary>Transcribed 是引擎转出来的原话；UI 即时显示。</summary>
    public sealed record AsrTestResult(bool Ok, string Detail, int LatencyMs, string Transcribed);

    public sealed record TtsTestResult(bool Ok, string Detail, int LatencyMs, string? AudioBase64, double Seconds);

    public static void Map(WebApplication app, ApiContext ctx)
    {
        app.MapPost("/api/voice/test/asr", async (CancellationToken ct) =>
            Results.Ok(await TestAsrAsync(ctx, ct).ConfigureAwait(false)));

        app.MapPost("/api/voice/test/tts", async (CancellationToken ct) =>
            Results.Ok(await TestTtsAsync(ctx, ct).ConfigureAwait(false)));

        app.MapPost("/api/voice/test/kws", async (CancellationToken ct) =>
            Results.Ok(await TestKwsAsync(ctx, ct).ConfigureAwait(false)));

        app.MapPost("/api/voice/test/model", async (CancellationToken ct) =>
            Results.Ok(await TestModelAsync(ctx, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// ASR 实测：在配置的输入设备上实听 2 秒，把录下的音频喂给引擎、返回识别文本——用户看到的
    /// 就是这台引擎对他嗓音的转写。设备打不开退回启动级静音探测（只有线路连通性可判）。
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
            var cleaned = text.Trim();
            var reply = cleaned.Length > 0
                ? $"识别到：「{cleaned}」（{note}）"
                : $"可达但没识别出内容（{note}）";
            return new AsrTestResult(true, reply, (int)watch.ElapsedMilliseconds, cleaned);
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

    /// <summary>TTS 实测：合成一句完整话，音频以 base64 WAV 回 UI 播放——用户听到的是这台引擎的真实声音。</summary>
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

    /// <summary>在指定输入设备上实听 seconds 秒，收满一段 float32 PCM。独占的是同一只麦：
    /// 语音输入半区若在跑会互踩——宿主保证先停（Reconfigure 已有门），测试级直接开录。</summary>
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

    /// <summary>16-bit 单声道 WAV（voices 的 PcmWav 是 Partner.Voice internal，这里自拼一份与它同格式）。</summary>
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
    /// KWS: needs the engine's own health endpoint rather than an utterance — a wake probe feeds a stream
    /// stateful across calls, so a synthetic "hello" would pollute the very state being tested. Reads
    /// keywords too, so the settings page can show the live word list next to its editor.
    /// </summary>
    private static async Task<VoiceTestResult> TestKwsAsync(ApiContext ctx, CancellationToken ct)
    {
        var settings = ctx.Settings.Load();
        var base_ = settings.Asr.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_) || AsrProtocols.Normalize(settings.Asr.Protocol) != AsrProtocols.SenseVoice)
        {
            return new VoiceTestResult(false, "未配置 sensevoice 协议的 ASR 地址（KWS 与 ASR 同引擎）", 0);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
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
                // /keywords 是本仓壳的扩展，第三方 sherpa 服务没有它不算失败。
            }

            var keywordText = keywords.Length > 0 ? $"：{string.Join("、", keywords)}" : "";
            var kwsText = kws ? "已启用" : "未启用";
            return new VoiceTestResult(
                true,
                $"可达，唤醒词{kwsText}{keywordText}",
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
    /// Text model: one 1-token chat completion proves key+url+model all at once. Vision: a 1×1 red PNG
    /// offered the same way — models that accept it say 2xx; text-only endpoints usually 400. The flag is
    /// reported to the user rather than trusted for the desktop tool table (that stays SupportsImages).
    /// </summary>
    private static async Task<VoiceTestResult> TestModelAsync(ApiContext ctx, CancellationToken ct)
    {
        var settings = ctx.Settings.Load();
        var base_ = settings.Model.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_) || string.IsNullOrWhiteSpace(settings.Model.Model))
        {
            return new VoiceTestResult(false, "未配置 model.baseUrl 或 model.model", 0);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.Model.TimeoutSeconds > 0 ? settings.Model.TimeoutSeconds : 30) };
        if (!string.IsNullOrEmpty(settings.Model.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Model.ApiKey);
        }

        var chat = $"{base_}/chat/completions";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 视觉探测先发：最贵的就是这次请求，两次合成请求太浪费。
            var vision = await ChatAsync(http, chat, VisionBody(settings.Model.Model), ct).ConfigureAwait(false);
            var visionOk = vision is not null;

            var text = await ChatAsync(http, chat, TextBody(settings.Model.Model), ct).ConfigureAwait(false);
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

    private static string TextBody(string model) =>
        "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":1}";

    // 1×1 红色 PNG，data URI：够证明端点收 multimodal 消息，也小到不占任何统计。
    private static string VisionBody(string model) =>
        "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":[" +
        "{\"type\":\"text\",\"text\":\"ping\"}," +
        "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==\"}}" +
        "]}],\"max_tokens\":1}";
}
