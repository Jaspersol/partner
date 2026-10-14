using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Partner.Voice.Asr;

namespace Partner.Voice.Tests.Asr;

public sealed class LocalAsrClientTests
{
    [Fact]
    public async Task Transcribe_posts_raw_float32_pcm_and_reads_text()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"text":"你好世界"}""")));
        string result;
        using (client)
        {
            result = await client.TranscribeAsync(new[] { 0.25f, -0.5f, 1f });
        }

        Assert.Equal("你好世界", result);
        var uri = handler.LastUri!;
        Assert.Equal("/recognize", uri.AbsolutePath);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(45103, uri.Port);
        Assert.Equal("application/octet-stream", handler.LastContentType);

        // 请求体就是原始小端 float32：三个采样 12 字节。
        var bytes = Convert.FromBase64String(handler.LastBodyRaw!);
        Assert.Equal(12, bytes.Length);
        Assert.Equal(0.25f, BitConverter.ToSingle(bytes, 0));
        Assert.Equal(-0.5f, BitConverter.ToSingle(bytes, 4));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 8));
    }

    [Fact]
    public async Task Missing_text_field_yields_empty_transcript()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json("""{"error":"denied"}""")));
        using (client)
        {
            Assert.Equal(string.Empty, await client.TranscribeAsync(new float[] { 1f }));
        }
    }

    [Fact]
    public async Task Http_error_throws_with_status_in_message()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("engine busy", Encoding.UTF8, "text/plain"),
        }));

        using (client)
        {
            var error = await Assert.ThrowsAsync<AsrRequestException>(() => client.TranscribeAsync(new float[] { 1f }));
            Assert.Contains("503", error.Message);
            Assert.Contains("engine busy", error.Message);
        }
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
            await Assert.ThrowsAsync<AsrTimeoutException>(() => client.TranscribeAsync(new float[] { 1f }));
        }
    }

    [Fact]
    public async Task Stream_mode_yields_the_full_transcript_once()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"text":"one shot"}""")));
        var deltas = new List<string>();
        using (client)
        {
            await foreach (var delta in client.TranscribeStreamAsync(new float[] { 1f }))
            {
                deltas.Add(delta);
            }
        }

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("one shot", string.Concat(deltas));
    }

    private static (LocalAsrClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null)
    {
        var handler = new RecordingHandler(respond);
        var options = new AsrOptions
        {
            BaseUrl = "http://127.0.0.1:45103",
            Protocol = AsrProtocols.SenseVoice,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2),
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(3),
        };
        return (new LocalAsrClient(options, handler), handler);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public Uri? LastUri { get; private set; }

        public string? LastBody { get; private set; }

        /// <summary>二进制体（octet-stream）的 base64；文本体原样。</summary>
        public string? LastBodyRaw { get; private set; }

        public string? LastContentType { get; private set; }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUri = request.RequestUri;
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                LastBodyRaw = Convert.ToBase64String(bytes);
            }

            return await _respond(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class LocalWakeClientTests
{
    [Fact]
    public async Task Poll_parses_a_hit()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"keyword":"小爱同学","seq":12,"confidence":0.87}""")));

        using (client)
        {
            var hit = await client.PollAsync(11);
            Assert.Equal("小爱同学", hit.Keyword);
            Assert.Equal(12, hit.Seq);
            Assert.Equal(0.87, hit.Confidence);
            Assert.True(hit.IsHit);
        }

        Assert.Contains("seq=11", handler.LastQuery);
    }

    [Fact]
    public async Task Poll_parses_a_miss()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json("""{"seq":9}""")));
        using (client)
        {
            var hit = await client.PollAsync(9);
            Assert.Null(hit.Keyword);
            Assert.False(hit.IsHit);
            Assert.Equal(9, hit.Seq);
        }
    }

    [Fact]
    public async Task Missing_fields_fall_back_to_the_watermark()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json("""{}""")));
        using (client)
        {
            var hit = await client.PollAsync(42);
            Assert.Equal(42, hit.Seq);
            Assert.False(hit.IsHit);
        }
    }

    private static (LocalWakeClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new RecordingHandler(respond);
        return (new LocalWakeClient("http://127.0.0.1:45103", handler), handler);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public string? LastQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastQuery = request.RequestUri!.Query;
            return _respond(request, cancellationToken);
        }
    }
}
