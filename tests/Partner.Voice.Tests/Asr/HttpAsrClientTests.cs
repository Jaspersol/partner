using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Partner.Voice.Asr;

namespace Partner.Voice.Tests.Asr;

public sealed class HttpAsrClientTests
{
    private const string PrefilledBody =
        """{"choices":[{"message":{"content":"language Chinese<asr_text>hello there"}}]}""";

    [Fact]
    public async Task Transcribe_sends_openai_compatible_payload_with_wav_audio()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(PrefilledBody)));
        string result;
        using (client)
        {
            result = await client.TranscribeAsync(Pcm(160));
        }

        Assert.Equal("hello there", result);
        var uri = handler.LastUri!;
        Assert.Equal("/v1/chat/completions", uri.AbsolutePath);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(45101, uri.Port);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.Equal("qwen3-asr", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());

        var audio = messages[0].GetProperty("content")[0];
        Assert.Equal("input_audio", audio.GetProperty("type").GetString());
        var inputAudio = audio.GetProperty("input_audio");
        Assert.Equal("wav", inputAudio.GetProperty("format").GetString());

        var wav = Convert.FromBase64String(inputAudio.GetProperty("data").GetString()!);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));       // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // mono
        Assert.Equal(16_000, BitConverter.ToInt32(wav, 24));  // sample rate
        Assert.Equal(32_000, BitConverter.ToInt32(wav, 28));  // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));       // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));      // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(320, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + 320, wav.Length);

        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("language Chinese<asr_text>",
            messages[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task No_prefill_message_when_language_is_null()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json("""{"choices":[{"message":{"content":"hi"}}]}""")), language: null);
        using (client)
        {
            await client.TranscribeAsync(Pcm(8));
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.DoesNotContain("assistant", handler.LastBody);
    }

    [Fact]
    public async Task Non_streaming_response_is_stripped_and_trimmed()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(PrefilledBody)));
        using (client)
        {
            Assert.Equal("hello there", await client.TranscribeAsync(Pcm(8)));
        }
    }

    [Fact]
    public async Task Streaming_response_yields_ordered_deltas_and_never_leaks_the_prefix()
    {
        const string sse =
            ": keep-alive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"language Chinese<asr_text>hello\"}}]}\n\n" +
            "data: {\"choices\":[]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" ignored\"}}]}\n\n";

        var (client, handler) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new SlicedContent(Encoding.UTF8.GetBytes(sse), sliceSize: 3),
        }));

        var deltas = new List<string>();
        using (client)
        {
            await foreach (var delta in client.TranscribeStreamAsync(Pcm(8)))
            {
                deltas.Add(delta);
            }
        }

        Assert.Equal(new[] { "hello", " there" }, deltas);
        Assert.Equal("hello there", string.Concat(deltas));
        Assert.Contains("\"stream\":true", handler.LastBody);
    }

    [Fact]
    public async Task Read_timeout_throws_AsrTimeoutException()
    {
        var (client, _) = Create(
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Json(PrefilledBody);
            },
            connectTimeout: TimeSpan.FromSeconds(30),
            readTimeout: TimeSpan.FromMilliseconds(200));

        using (client)
        {
            await Assert.ThrowsAsync<AsrTimeoutException>(() => client.TranscribeAsync(Pcm(8)));
        }
    }

    [Fact]
    public async Task Streaming_read_timeout_throws_AsrTimeoutException()
    {
        var (client, _) = Create(
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Json(PrefilledBody);
            },
            connectTimeout: TimeSpan.FromSeconds(30),
            readTimeout: TimeSpan.FromMilliseconds(200));

        using (client)
        {
            await Assert.ThrowsAsync<AsrTimeoutException>(async () =>
            {
                await foreach (var _ in client.TranscribeStreamAsync(Pcm(8)))
                {
                }
            });
        }
    }

    [Fact]
    public async Task Http_error_throws_AsrRequestException_with_status()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("model exploded", Encoding.UTF8, "text/plain"),
        }));

        using (client)
        {
            var error = await Assert.ThrowsAsync<AsrRequestException>(() => client.TranscribeAsync(Pcm(8)));
            Assert.Contains("500", error.Message);
            Assert.Contains("model exploded", error.Message);
        }
    }

    [Fact]
    public async Task Http_error_during_streaming_throws_AsrRequestException_with_status()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream down", Encoding.UTF8, "text/plain"),
        }));

        using (client)
        {
            var error = await Assert.ThrowsAsync<AsrRequestException>(async () =>
            {
                await foreach (var _ in client.TranscribeStreamAsync(Pcm(8)))
                {
                }
            });
            Assert.Contains("502", error.Message);
        }
    }

    [Fact]
    public void Dispose_does_not_dispose_an_injected_handler()
    {
        var handler = new DisposableHandler();
        var options = new AsrOptions { BaseUrl = "http://127.0.0.1:45101" };
        using (var client = new HttpAsrClient(options, handler))
        {
            client.Dispose();
            client.Dispose();
            Assert.False(handler.IsDisposed);
        }

        handler.Dispose();
        Assert.True(handler.IsDisposed);
    }

    private static (HttpAsrClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string? language = "Chinese",
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null)
    {
        var handler = new RecordingHandler(respond);
        var options = new AsrOptions
        {
            BaseUrl = "http://127.0.0.1:45101",
            Language = language,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2),
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(3),
        };
        return (new HttpAsrClient(options, handler), handler);
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

    private sealed class DisposableHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Returns content in fixed-size slices, to exercise SSE lines split across TCP reads.</summary>
    private sealed class SlicedContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly int _sliceSize;

        public SlicedContent(byte[] data, int sliceSize)
        {
            _data = data;
            _sliceSize = sliceSize;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new SlicedStream(_data, _sliceSize));
    }

    private sealed class SlicedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _sliceSize;
        private int _position;

        public SlicedStream(byte[] data, int sliceSize)
        {
            _data = data;
            _sliceSize = sliceSize;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var size = NextSize(count);
            if (size == 0)
            {
                return 0;
            }

            Array.Copy(_data, _position, buffer, offset, size);
            _position += size;
            return size;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var size = NextSize(buffer.Length);
            if (size == 0)
            {
                return ValueTask.FromResult(0);
            }

            _data.AsMemory(_position, size).CopyTo(buffer);
            _position += size;
            return ValueTask.FromResult(size);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int NextSize(int requested) => Math.Min(Math.Min(requested, _sliceSize), _data.Length - _position);
    }
}

public sealed class AsrMetadataTests
{
    [Theory]
    [InlineData("language Chinese<asr_text>hello", "hello")]
    [InlineData("  language Chinese<asr_text>hello", "hello")]
    [InlineData("language Chinese<asr_text> hello ", "hello ")]
    [InlineData("LANGUAGE chinese<asr_text>hello", "hello")]
    [InlineData("language Chinese<asr_text>", "")]
    [InlineData("hello there", "hello there")]
    [InlineData("the language model said hello", "the language model said hello")]
    [InlineData("what language is this<asr_text>", "what language is this<asr_text>")]
    public void Strip_removes_only_a_leading_metadata_prefix(string input, string expected) =>
        Assert.Equal(expected, AsrMetadata.Strip(input));
}
