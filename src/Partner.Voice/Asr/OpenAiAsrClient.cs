using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Partner.Voice.Asr;

/// <summary>
/// Generic ASR HTTP client: OpenAI standard <c>POST {BaseUrl}/audio/transcriptions</c>, WAV uploaded as
/// multipart/form-data, parsing a <c>{ "text": "..." }</c> response.
/// <para>
/// Unlike <see cref="HttpAsrClient"/> it carries no <c>language X&lt;asr_text&gt;</c> metadata and prefills no assistant
/// turn — standard endpoints such as whisper / funasr accept only <c>file</c>, <c>model</c> and <c>language</c>.
/// </para>
/// </summary>
public sealed class OpenAiAsrClient : IAsrClient
{
    private const int ErrorBodyLimit = 512;

    private readonly AsrOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private bool _disposed;

    public OpenAiAsrClient(AsrOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ownsClient = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/audio/transcriptions", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        using var request = CreateRequest(pcm);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AsrRequestException(
                $"ASR request failed with status {(int)response.StatusCode} ({response.StatusCode}): {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);
        return ReadText(document.RootElement) ?? string.Empty;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TranscribeStreamAsync(
        float[] pcm,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The standard endpoint has no SSE streaming transcription: return the full text once and let the caller treat it as a delta.
        var text = await TranscribeAsync(pcm, ct).ConfigureAwait(false);
        if (text.Length > 0)
        {
            yield return text;
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

    private HttpRequestMessage CreateRequest(float[] pcm)
    {
        var wav = PcmWav.Encode16(pcm, _options.SampleRate);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        content.Add(new StringContent(_options.Model), "model");
        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            content.Add(new StringContent(_options.Language), "language");
        }

        return new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_options.ConnectTimeout);
        try
        {
            return await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                .WaitAsync(_options.ReadTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
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

    private static string? ReadText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return text.GetString();
    }

    private static string Truncate(string body) =>
        body.Length <= ErrorBodyLimit ? body : body[..ErrorBodyLimit] + "…";
}
