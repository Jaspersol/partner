using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Partner.Voice.Asr;

/// <summary>
/// ASR HTTP client: <c>POST {BaseUrl}/v1/chat/completions</c> with 16-bit PCM WAV inlined as base64.
/// <para>
/// Connect and read timeouts are separate: <see cref="HttpClient.Timeout"/> is infinite, the connect phase uses
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>, and the response phase uses
/// <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>.
/// Both timeouts throw <see cref="AsrTimeoutException"/>, and the caller retries the whole audio block.
/// </para>
/// </summary>
public sealed class HttpAsrClient : IAsrClient
{
    private const int ErrorBodyLimit = 512;

    private readonly AsrOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private bool _disposed;

    public HttpAsrClient(AsrOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ownsClient = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/v1/chat/completions", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        using var request = CreateRequest(pcm, stream: false);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AsrRequestException(
                $"ASR request failed with status {(int)response.StatusCode} ({response.StatusCode}): {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var content = ReadMessageContent(document.RootElement);
        return AsrMetadata.Strip(content ?? string.Empty).Trim();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TranscribeStreamAsync(
        float[] pcm,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        using var request = CreateRequest(pcm, stream: true);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            throw new AsrRequestException(
                $"ASR request failed with status {(int)response.StatusCode} ({response.StatusCode}): {Truncate(error)}");
        }

        var accumulated = new StringBuilder();
        var yielded = 0;
        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8);

        while (true)
        {
            var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // blank line, SSE comment (": ..."), or another field
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                break;
            }

            var delta = ReadDeltaContent(payload);
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            accumulated.Append(delta);
            var stripped = AsrMetadata.Strip(accumulated.ToString());
            if (stripped.Length > yielded)
            {
                yield return stripped[yielded..];
                yielded = stripped.Length;
            }
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

    private HttpRequestMessage CreateRequest(float[] pcm, bool stream)
    {
        var content = new ByteArrayContent(BuildPayload(pcm, stream));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
    }

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Base64 and the + / < / > in <asr_text> need no \uXXXX escaping: saves bytes and keeps the wire payload compact.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private byte[] BuildPayload(float[] pcm, bool stream)
    {
        var audio = Convert.ToBase64String(PcmWav.Encode16(pcm, _options.SampleRate));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);

            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "input_audio");
            writer.WriteStartObject("input_audio");
            writer.WriteString("data", audio);
            writer.WriteString("format", "wav");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            if (_options.Language is not null)
            {
                // Prefill the assistant turn so the model emits the transcript directly instead of "language X<asr_text>" metadata.
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", $"language {_options.Language}<asr_text>");
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("stream", stream);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_options.ConnectTimeout);
        var send = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
        try
        {
            return await send.WaitAsync(_options.ReadTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            connectCts.Cancel(); // abandon the stalled request rather than leave the connection in the pool
            throw new AsrTimeoutException(
                $"ASR response timed out after {_options.ReadTimeout.TotalMilliseconds:0} ms.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AsrTimeoutException(
                $"ASR connect timed out after {_options.ConnectTimeout.TotalMilliseconds:0} ms.", ex);
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_options.ReadTimeout);
        try
        {
            return await response.Content.ReadAsStringAsync(readCts.Token)
                .WaitAsync(_options.ReadTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new AsrTimeoutException(
                $"ASR body read timed out after {_options.ReadTimeout.TotalMilliseconds:0} ms.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AsrTimeoutException(
                $"ASR body read timed out after {_options.ReadTimeout.TotalMilliseconds:0} ms.", ex);
        }
    }

    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct).AsTask().WaitAsync(_options.ReadTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new AsrTimeoutException(
                $"ASR stream stalled for more than {_options.ReadTimeout.TotalMilliseconds:0} ms.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AsrTimeoutException(
                $"ASR stream stalled for more than {_options.ReadTimeout.TotalMilliseconds:0} ms.", ex);
        }
    }

    private static string? ReadMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return content.GetString();
    }

    private static string? ReadDeltaContent(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return content.GetString();
    }

    private static string Truncate(string body) =>
        body.Length <= ErrorBodyLimit ? body : body[..ErrorBodyLimit] + "…";
}
