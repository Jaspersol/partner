using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Partner.Voice.Tts;

/// <summary>
/// TTS HTTP client: <c>POST {BaseUrl}/tts</c>; the response body is a raw little-endian float32 PCM stream,
/// split into <see cref="TtsOptions.ChunkMs"/> chunks as it arrives and handed to the player.
/// </summary>
public sealed class HttpTtsClient : ITtsClient
{
    private const int ErrorBodyLimit = 512;
    private const int ReadBufferSize = 8192;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Reference audio base64 and Chinese text need not be escaped to \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TtsOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private readonly int _chunkSamples;
    private readonly string? _speakerRefBase64;
    private bool _disposed;

    public HttpTtsClient(TtsOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ownsClient = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/tts", UriKind.Absolute);
        _chunkSamples = Math.Max(1, options.SampleRate * options.ChunkMs / 1000);

        // Reference audio is read and encoded once at construction and reused for every sentence.
        if (!string.IsNullOrEmpty(options.SpeakerRefPath))
        {
            _speakerRefBase64 = Convert.ToBase64String(File.ReadAllBytes(options.SpeakerRefPath));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<float[]> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.Timeout);
        var token = timeoutCts.Token;

        using var request = CreateRequest(text);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            throw new HttpRequestException(
                $"TTS request failed with status {(int)response.StatusCode} ({response.StatusCode}): {Truncate(error)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var decoder = new PcmFloatStreamReader();
        var buffer = new byte[ReadBufferSize];
        var carry = new float[_chunkSamples];
        var count = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var samples = decoder.Append(buffer.AsSpan(0, read));
            if (samples.Length == 0)
            {
                continue;
            }

            if (count + samples.Length > carry.Length)
            {
                Array.Resize(ref carry, count + samples.Length);
            }

            samples.CopyTo(carry, count);
            count += samples.Length;

            while (count >= _chunkSamples)
            {
                var chunk = new float[_chunkSamples];
                Array.Copy(carry, 0, chunk, 0, _chunkSamples);
                Array.Copy(carry, _chunkSamples, carry, 0, count - _chunkSamples);
                count -= _chunkSamples;
                yield return chunk;
            }
        }

        decoder.DiscardPartialSample();
        if (count > 0)
        {
            var tail = new float[count];
            Array.Copy(carry, 0, tail, 0, count);
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
            writer.WriteString("input", text);
            writer.WriteString("lang", _options.Lang);
            writer.WriteBoolean("stream", true);
            writer.WriteString("response_format", "pcm");
            if (_speakerRefBase64 is not null)
            {
                writer.WriteString("speaker_ref_b64", _speakerRefBase64);
            }

            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
    }

    private static string Truncate(string body) =>
        body.Length <= ErrorBodyLimit ? body : body[..ErrorBodyLimit] + "…";
}
