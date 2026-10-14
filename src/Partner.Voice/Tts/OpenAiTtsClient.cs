using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Partner.Voice.Tts;

/// <summary>
/// 通用 TTS HTTP 客户端：OpenAI 标准 <c>POST {BaseUrl}/audio/speech</c>，请求
/// <c>{ model, input, voice, response_format: "wav" }</c>，响应体是完整 WAV，一次性合
/// 成到内存再切成 <see cref="TtsOptions.ChunkMs"/> 块播放。
/// <para>
/// 与 <see cref="HttpTtsClient"/>（自建服务的裸 PCM 流）不同：标准端点几乎都不支持
/// 流式返回，且请求字段名不同（<c>input</c>/<c>voice</c>/<c>speed</c> 而不是 <c>text</c>/<c>lang</c>）。
/// 每次请求都到完整响应才出第一批音频——流式对这种端点的意义只剩下块切分。
/// </para>
/// </summary>
public sealed class OpenAiTtsClient : ITtsClient
{
    private const int ErrorBodyLimit = 512;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 中文文本不必转义成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TtsOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private readonly int _chunkSamples;
    private readonly string? _voice;
    private readonly double _speed;
    private bool _disposed;

    public OpenAiTtsClient(TtsOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ownsClient = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/audio/speech", UriKind.Absolute);
        _chunkSamples = Math.Max(1, options.SampleRate * options.ChunkMs / 1000);
        _voice = string.IsNullOrWhiteSpace(options.Voice) ? "alloy" : options.Voice;
        _speed = options.Speed <= 0 ? 1.0 : options.Speed;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<float[]> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var request = CreateRequest(text);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"TTS request failed with status {(int)response.StatusCode} ({response.StatusCode}): {Truncate(error)}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var samples = WavPcm16Decoder.Decode(bytes);

        var position = 0;
        while (position + _chunkSamples <= samples.Length)
        {
            var chunk = new float[_chunkSamples];
            Array.Copy(samples, position, chunk, 0, _chunkSamples);
            position += _chunkSamples;
            yield return chunk;
        }

        if (position < samples.Length)
        {
            var tail = new float[samples.Length - position];
            Array.Copy(samples, position, tail, 0, tail.Length);
            yield return tail;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model ?? "tts-1");
            writer.WriteString("input", text);
            writer.WriteString("voice", _voice);
            writer.WriteString("response_format", "wav");
            writer.WriteNumber("speed", _speed);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
    }

    private static string Truncate(string body) =>
        body.Length <= ErrorBodyLimit ? body : body[..ErrorBodyLimit] + "…";
}
