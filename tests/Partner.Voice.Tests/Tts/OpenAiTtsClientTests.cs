using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Partner.Voice.Tts;

namespace Partner.Voice.Tests.Tts;

public sealed class OpenAiTtsClientTests
{
    [Fact]
    public async Task Synthesize_posts_openai_speech_payload_and_decodes_wav()
    {
        var wav = Wav(new[] { 0.5f, -0.25f, 1f, -1f });
        var (client, handler) = Create((_, _) => Task.FromResult(WavResponse(wav)));
        var chunks = new List<float[]>();
        using (client)
        {
            await foreach (var chunk in client.SynthesizeAsync("你好，世界。"))
            {
                chunks.Add(chunk);
            }
        }

        var uri = handler.LastUri!;
        Assert.Equal("/audio/speech", uri.AbsolutePath);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(45102, uri.Port);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.Equal("你好，世界。", root.GetProperty("input").GetString());
        Assert.Equal("tts-1", root.GetProperty("model").GetString());
        Assert.Equal("alloy", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.Equal(1.0, root.GetProperty("speed").GetDouble());

        var decoded = chunks.SelectMany(c => c).ToArray();
        var expected = new[] { 0.5f, -0.25f, 1f, -1f };
        for (var i = 0; i < Math.Max(expected.Length, decoded.Length); i++)
        {
            var e = i < expected.Length ? expected[i] : 0f;
            var d = i < decoded.Length ? decoded[i] : 0f;
            Assert.True(Math.Abs(e - d) < 2 / 32768f, $"sample {i}: expected {e}, got {d}");
        }
    }

    [Fact]
    public async Task Chunking_matches_chunk_ms_boundaries()
    {
        var samples = new float[6000];
        var (client, _) = Create((_, _) => Task.FromResult(WavResponse(Wav(samples))));
        var chunks = new List<float[]>();
        using (client)
        {
            await foreach (var chunk in client.SynthesizeAsync("hello"))
            {
                chunks.Add(chunk);
            }
        }

        // 默认 24 kHz × 100 ms = 2400 采样。
        Assert.Equal(new[] { 2400, 2400, 1200 }, chunks.Select(c => c.Length));
    }

    [Fact]
    public async Task Multichannel_wav_takes_the_first_channel()
    {
        // 交错两路：左路 0.5, -0.25, 1, -1；右路全 0。解码只取左路。
        var interleave = new[] { 0.5f, 0f, -0.25f, 0f, 1f, 0f, -1f, 0f };
        var (client, _) = Create((_, _) => Task.FromResult(WavResponse(WavInterleaved(interleave, 2))));
        List<float> decoded;
        using (client)
        {
            decoded = new List<float>();
            await foreach (var chunk in client.SynthesizeAsync("hello"))
            {
                decoded.AddRange(chunk);
            }
        }

        var expected = new[] { 0.5f, -0.25f, 1f, -1f };
        for (var i = 0; i < Math.Max(expected.Length, decoded.Count); i++)
        {
            var e = i < expected.Length ? expected[i] : 0f;
            var d = i < decoded.Count ? decoded[i] : 0f;
            Assert.True(Math.Abs(e - d) < 2 / 32768f, $"sample {i}: expected {e}, got {d}");
        }
    }

    [Fact]
    public async Task Voice_model_and_speed_are_taken_from_options_and_defaulted()
    {
        var (client, handler) = Create(
            (_, _) => Task.FromResult(WavResponse(Wav(Array.Empty<float>()))),
            model: "gpt-4o-mini-tts",
            voice: "echo",
            speed: 1.25);
        using (client)
        {
            await foreach (var _ in client.SynthesizeAsync("hi")) { }
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.Equal("gpt-4o-mini-tts", root.GetProperty("model").GetString());
        Assert.Equal("echo", root.GetProperty("voice").GetString());
        Assert.Equal(1.25, root.GetProperty("speed").GetDouble());
    }

    [Fact]
    public async Task Missing_voice_speed_and_model_default_in_request()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(WavResponse(Wav(Array.Empty<float>()))));
        using (client)
        {
            await foreach (var _ in client.SynthesizeAsync("hi")) { }
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.Equal("tts-1", root.GetProperty("model").GetString());
        Assert.Equal("alloy", root.GetProperty("voice").GetString());
        Assert.Equal(1.0, root.GetProperty("speed").GetDouble());
    }

    [Fact]
    public async Task Empty_body_yields_no_chunks()
    {
        var (client, _) = Create((_, _) => Task.FromResult(WavResponse(Wav(Array.Empty<float>()))));
        using (client)
        {
            Assert.Empty(await Drain(client.SynthesizeAsync("hello")));
        }
    }

    [Fact]
    public async Task Non_wav_body_throws_a_readable_error()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not actually wav", Encoding.UTF8, "text/plain"),
        }));

        using (client)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Drain(client.SynthesizeAsync("hello")));
            Assert.Contains("RIFF", error.Message);
        }
    }

    [Fact]
    public async Task Http_error_throws_with_status_in_message()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("no model loaded", Encoding.UTF8, "text/plain"),
        }));

        using (client)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => Drain(client.SynthesizeAsync("hello")));
            Assert.Contains("503", error.Message);
            Assert.Contains("no model loaded", error.Message);
        }
    }

    [Fact]
    public async Task The_local_engine_is_sent_no_voice_unless_one_is_chosen()
    {
        var wav = Wav(new[] { 0.5f });
        foreach (var (voice, expected) in new (string?, string?)[] { (null, null), ("晓晓", "晓晓") })
        {
            var (client, handler) = Create((_, _) => Task.FromResult(WavResponse(wav)), voice: voice, protocol: TtsProtocols.Local);
            using (client)
            {
                await foreach (var _ in client.SynthesizeAsync("hi")) { }
            }

            using var document = JsonDocument.Parse(handler.LastBody!);
            Assert.Equal(expected is not null, document.RootElement.TryGetProperty("voice", out var sent));
            if (expected is not null)
            {
                Assert.Equal(expected, sent.GetString());
            }
        }
    }

    [Fact]
    public async Task A_clone_clip_is_what_the_local_engine_is_asked_to_speak_with()
    {
        var (client, handler) = Create(
            (_, _) => Task.FromResult(WavResponse(Wav(new[] { 0.5f }))),
            voice: "晓晓", protocol: TtsProtocols.Local, speakerRefPath: @"C:woicesme.wav");
        using (client)
        {
            await foreach (var _ in client.SynthesizeAsync("hi")) { }
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(@"C:woicesme.wav", document.RootElement.GetProperty("voice").GetString());
    }

    [Fact]
    public async Task A_clone_clip_carries_its_transcript_to_the_local_engine()
    {
        var (client, handler) = Create(
            (_, _) => Task.FromResult(WavResponse(Wav(new[] { 0.5f }))),
            protocol: TtsProtocols.Local,
            speakerRefPath: @"C:\voices\me.wav",
            speakerRefText: "  你好，很高兴认识你。  ");
        using (client)
        {
            await foreach (var _ in client.SynthesizeAsync("hi")) { }
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("你好，很高兴认识你。", document.RootElement.GetProperty("reference_text").GetString());
    }

    [Fact]
    public async Task The_transcript_is_not_sent_to_a_generic_openai_endpoint()
    {
        var (client, handler) = Create(
            (_, _) => Task.FromResult(WavResponse(Wav(new[] { 0.5f }))),
            protocol: TtsProtocols.OpenAi,
            speakerRefText: "你好。");
        using (client)
        {
            await foreach (var _ in client.SynthesizeAsync("hi")) { }
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.False(document.RootElement.TryGetProperty("reference_text", out _));
    }

    private static (OpenAiTtsClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string? model = null,
        string? voice = null,
        double speed = 0,
        string protocol = TtsProtocols.OpenAi,
        string? speakerRefPath = null,
        string? speakerRefText = null)
    {
        var handler = new RecordingHandler(respond);
        var options = new TtsOptions
        {
            BaseUrl = "http://127.0.0.1:45102",
            Protocol = protocol,
            Model = model,
            Voice = voice,
            SpeakerRefPath = speakerRefPath,
            SpeakerRefText = speakerRefText,
            Speed = speed,
        };
        return (new OpenAiTtsClient(options, handler), handler);
    }

    private static async Task<List<float[]>> Drain(IAsyncEnumerable<float[]> chunks)
    {
        var all = new List<float[]>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    /// <summary>把 [-1,1] float 采样编成 16-bit 单声道 PCM WAV，客户端的反向操作。</summary>
    private static byte[] Wav(float[] samples) => WavInterleaved(samples, channels: 1);

    /// <summary>把交错采样编成 16-bit PCM WAV（多声道时输入按帧交错）。</summary>
    private static byte[] WavInterleaved(float[] samples, short channels)
    {
        const int headerSize = 44;
        var dataBytes = samples.Length * sizeof(short);
        var wav = new byte[headerSize + dataBytes];
        Encoding.ASCII.GetBytes("RIFF", wav.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE", wav.AsSpan(8, 4));
        Encoding.ASCII.GetBytes("fmt ", wav.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), 24_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), 24_000 * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data", wav.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);

        var offset = headerSize;
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            var raw = (short)MathF.Round(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset, 2), raw);
            offset += 2;
        }

        return wav;
    }

    private static HttpResponseMessage WavResponse(byte[] wav) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(wav),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public Uri? LastUri { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return await _respond(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
