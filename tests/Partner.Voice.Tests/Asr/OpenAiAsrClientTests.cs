using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Partner.Voice.Asr;

namespace Partner.Voice.Tests.Asr;

public sealed class OpenAiAsrClientTests
{
    [Fact]
    public async Task Transcribe_posts_multipart_to_transcriptions_and_reads_text()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"text":"hello there"}""")));
        string result;
        using (client)
        {
            result = await client.TranscribeAsync(Pcm(160));
        }

        Assert.Equal("hello there", result);
        var uri = handler.LastUri!;
        Assert.Equal("/audio/transcriptions", uri.AbsolutePath);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(45101, uri.Port);
        Assert.StartsWith("multipart/form-data", handler.LastContentType);

        var body = handler.LastBody!;
        Assert.Contains("Content-Disposition: form-data; name=model", body);
        Assert.Contains("whisper-1", body);
        Assert.Contains("Content-Disposition: form-data; name=language", body);
        Assert.Contains("Chinese", body);
        Assert.Contains("name=file", body);
        Assert.Contains("filename", body);
        Assert.Contains("audio/wav", body);

        // WAV 正文真实在 multipart 里，头是 PCM 16-bit 单声道。
        Assert.Contains("RIFF", body);
        Assert.Contains("WAVE", body);
        Assert.DoesNotContain("language Chinese<asr_text>", body);
    }

    [Fact]
    public async Task Language_omitted_when_null()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"text":"hi"}""")), language: null);
        using (client)
        {
            await client.TranscribeAsync(Pcm(8));
        }

        Assert.DoesNotContain("name=language", handler.LastBody);
    }

    [Fact]
    public async Task Empty_text_response_yields_empty_transcript()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json("""{"text":""}""")));
        using (client)
        {
            Assert.Equal(string.Empty, await client.TranscribeAsync(Pcm(8)));
        }
    }

    [Fact]
    public async Task Missing_text_field_yields_empty_transcript()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json("""{"error":{"message":"no"}}""")));
        using (client)
        {
            Assert.Equal(string.Empty, await client.TranscribeAsync(Pcm(8)));
        }
    }

    [Fact]
    public async Task Http_error_throws_with_status_in_message()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("model not loaded", Encoding.UTF8, "text/plain"),
        }));

        using (client)
        {
            var error = await Assert.ThrowsAsync<AsrRequestException>(() => client.TranscribeAsync(Pcm(8)));
            Assert.Contains("503", error.Message);
            Assert.Contains("model not loaded", error.Message);
        }
    }

    [Fact]
    public async Task Stream_mode_yields_the_full_transcript_once()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"text":"hello there"}""")));
        var deltas = new List<string>();
        using (client)
        {
            await foreach (var delta in client.TranscribeStreamAsync(Pcm(8)))
            {
                deltas.Add(delta);
            }
        }

        Assert.Equal(1, handler.RequestCount);
        var joined = string.Concat(deltas);
        Assert.Equal("hello there", joined);
    }

    [Fact]
    public async Task Read_timeout_throws_AsrTimeoutException()
    {
        var (client, _) = Create(
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Json("""{"text":"late"}""");
            },
            connectTimeout: TimeSpan.FromSeconds(30),
            readTimeout: TimeSpan.FromMilliseconds(200));

        using (client)
        {
            await Assert.ThrowsAsync<AsrTimeoutException>(() => client.TranscribeAsync(Pcm(8)));
        }
    }

    private static (OpenAiAsrClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string? language = "Chinese",
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null)
    {
        var handler = new RecordingHandler(respond);
        var options = new AsrOptions
        {
            BaseUrl = "http://127.0.0.1:45101",
            Model = "whisper-1",
            Language = language,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2),
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(3),
        };
        return (new OpenAiAsrClient(options, handler), handler);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static float[] Pcm(int count)
    {
        var pcm = new float[count];
        for (var i = 0; i < count; i++)
        {
            pcm[i] = (float)Math.Sin(i * 0.05) * 0.5f;
        }

        return pcm;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public Uri? LastUri { get; private set; }

        public string? LastBody { get; private set; }

        public string? LastContentType { get; private set; }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUri = request.RequestUri;
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return await _respond(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
