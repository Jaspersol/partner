using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Partner.Voice.Tts;

namespace Partner.Voice.Tests.Tts;

public sealed class HttpTtsClientTests
{
    [Fact]
    public async Task Synthesize_sends_payload_with_speaker_reference_when_configured()
    {
        var reference = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var path = WriteTempFile(reference);
        try
        {
            var (client, handler) = Create((_, _) => Task.FromResult(PcmResponse(Array.Empty<float>())), speakerRefPath: path);
            using (client)
            {
                await Drain(client.SynthesizeAsync("你好，世界。"));
            }

            Assert.Equal("/tts", handler.LastUri!.AbsolutePath);
            using var document = JsonDocument.Parse(handler.LastBody!);
            var root = document.RootElement;
            Assert.Equal("你好，世界。", root.GetProperty("input").GetString());
            Assert.Equal("zh", root.GetProperty("lang").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal("pcm", root.GetProperty("response_format").GetString());
            Assert.Equal(Convert.ToBase64String(reference), root.GetProperty("speaker_ref_b64").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Synthesize_omits_speaker_reference_when_not_configured()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(PcmResponse(Array.Empty<float>())));
        using (client)
        {
            await Drain(client.SynthesizeAsync("hello"));
        }

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.False(document.RootElement.TryGetProperty("speaker_ref_b64", out _));
    }

    [Fact]
    public async Task Synthesize_chunks_the_stream_at_chunk_ms_boundaries()
    {
        var samples = Pcm(5000);
        var (client, _) = Create((_, _) => Task.FromResult(PcmResponse(samples)));
        var chunks = new List<float[]>();
        using (client)
        {
            await foreach (var chunk in client.SynthesizeAsync("hello"))
            {
                chunks.Add(chunk);
            }
        }

        Assert.Equal(new[] { 2400, 2400, 200 }, chunks.Select(c => c.Length));
        Assert.Equal(samples, chunks.SelectMany(c => c));
    }

    [Fact]
    public async Task Synthesize_decodes_floats_split_across_reads()
    {
        var samples = Pcm(7);
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new SlicedContent(PcmBytes(samples), sliceSize: 3),
        }));

        var decoded = new List<float>();
        using (client)
        {
            await foreach (var chunk in client.SynthesizeAsync("hello"))
            {
                decoded.AddRange(chunk);
            }
        }

        Assert.Equal(samples, decoded);
    }

    [Fact]
    public async Task Empty_body_yields_no_chunks()
    {
        var (client, _) = Create((_, _) => Task.FromResult(PcmResponse(Array.Empty<float>())));
        using (client)
        {
            Assert.Empty(await Drain(client.SynthesizeAsync("hello")));
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

    private static (HttpTtsClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string? speakerRefPath = null)
    {
        var handler = new RecordingHandler(respond);
        var options = new TtsOptions
        {
            BaseUrl = "http://127.0.0.1:45102",
            SpeakerRefPath = speakerRefPath,
        };
        return (new HttpTtsClient(options, handler), handler);
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

    private static HttpResponseMessage PcmResponse(float[] samples) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(PcmBytes(samples)),
    };

    private static byte[] PcmBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), samples[i]);
        }

        return bytes;
    }

    private static float[] Pcm(int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.01) * 0.75f;
        }

        return samples;
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"partner-tts-ref-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        return path;
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

public sealed class PcmFloatStreamReaderTests
{
    [Fact]
    public void Append_returns_only_whole_samples_and_keeps_the_remainder()
    {
        var reader = new PcmFloatStreamReader();
        var bytes = PcmBytes(new[] { 1f, -2.5f });

        Assert.Equal(new[] { 1f }, reader.Append(bytes.AsSpan(0, 5)));
        Assert.Equal(1, reader.PendingBytes);

        Assert.Equal(new[] { -2.5f }, reader.Append(bytes.AsSpan(5)));
        Assert.Equal(0, reader.PendingBytes);

        Assert.Empty(reader.Append(bytes.AsSpan(0, 3)));
        Assert.Equal(3, reader.PendingBytes);
    }

    [Fact]
    public void DiscardPartialSample_returns_the_dropped_byte_count()
    {
        var reader = new PcmFloatStreamReader();
        Assert.Equal(0, reader.DiscardPartialSample());

        reader.Append(new byte[] { 1, 2, 3 });
        Assert.Equal(3, reader.PendingBytes);
        Assert.Equal(3, reader.DiscardPartialSample());
        Assert.Equal(0, reader.PendingBytes);

        reader.Append(new byte[] { 4, 5 });
        Assert.Equal(2, reader.DiscardPartialSample());
    }

    [Fact]
    public void Append_of_nothing_is_a_noop()
    {
        var reader = new PcmFloatStreamReader();
        reader.Append(new byte[] { 9 });

        Assert.Empty(reader.Append(ReadOnlySpan<byte>.Empty));
        Assert.Equal(1, reader.PendingBytes);
    }

    [Fact]
    public void Append_decodes_across_arbitrary_byte_boundaries()
    {
        var samples = new[] { 0.125f, -0.5f, 42f, float.MaxValue };
        var bytes = PcmBytes(samples);
        var reader = new PcmFloatStreamReader();
        var decoded = new List<float>();
        for (var i = 0; i < bytes.Length; i += 3)
        {
            decoded.AddRange(reader.Append(bytes.AsSpan(i, Math.Min(3, bytes.Length - i))));
        }

        Assert.Equal(0, reader.PendingBytes);
        Assert.Equal(samples, decoded);
    }

    private static byte[] PcmBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), samples[i]);
        }

        return bytes;
    }
}
