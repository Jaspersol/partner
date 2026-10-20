using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Partner.Voice.Asr;

/// <summary>
/// Local ASR HTTP client (<see cref="AsrProtocols.SenseVoice"/>):
/// <c>POST {BaseUrl}/recognize</c> with a raw little-endian float32 PCM body (16 kHz, no WAV wrapper on local
/// transport), returning <c>{ "text": … }</c>.
/// <para>
/// Unlike <see cref="OpenAiAsrClient"/>, the server is a local process, so the sample array is sent directly.
/// The engine cannot emit SSE, so streaming degrades to one full-text result.
/// </para>
/// </summary>
public sealed class LocalAsrClient : IAsrClient
{
    private const int ErrorBodyLimit = 512;

    private readonly AsrOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private bool _disposed;

    public LocalAsrClient(AsrOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ownsClient = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/recognize", UriKind.Absolute);
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
        var bytes = new byte[pcm.Length * sizeof(float)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
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

/// <summary>
/// Wake gate checker: the host hands a capture frame to the engine's KWS and gets one answer per call.
/// <para>
/// The implementation forwards to the engine's <c>POST /wake/infer</c> over HTTP (<see cref="LocalWakeClient"/>);
/// tests use a scripted hook.
/// </para>
/// </summary>
public interface IWakeChecker
{
    /// <summary>Feeds one capture frame and asks once. A non-empty keyword means a hit within this frame.</summary>
    Task<LocalWakeClient.WakeHit> PollAsync(float[] frame, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for wake word detection (companion endpoint of the local <see cref="AsrProtocols.SenseVoice"/> engine):
/// each <see cref="IWakeChecker.PollAsync"/> posts the capture frame to <c>POST /wake/infer</c>; a hit returns
/// <c>{ "hit": true, "keyword": …, "confidence": … }</c>, a miss returns <c>{ "hit": false }</c>.
/// </summary>
public sealed class LocalWakeClient : IWakeChecker, IDisposable
{
    private const int ErrorBodyLimit = 512;

    /// <summary>One hit or one empty probe.</summary>
    public sealed record WakeHit(string? Keyword, long Seq, double Confidence)
    {
        /// <summary>An empty keyword (anonymous engine hit) still counts as a hit: the hit bit is authoritative, not the name.</summary>
        public bool IsHit => Keyword is not null || Seq > 0 || Confidence > 0;
    }

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _inferEndpoint;
    private bool _disposed;

    public LocalWakeClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _ownsClient = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        _inferEndpoint = new Uri(baseUrl.TrimEnd('/') + "/wake/infer", UriKind.Absolute);
    }

    public async Task<WakeHit> PollAsync(float[] frame, CancellationToken ct = default)
    {
        var bytes = new byte[frame.Length * sizeof(float)];
        Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, _inferEndpoint) { Content = content };
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AsrRequestException(
                $"wake probe failed with status {(int)response.StatusCode} ({response.StatusCode}): {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var hit = root.TryGetProperty("hit", out var hitElement) && hitElement.ValueKind == JsonValueKind.True;
        var keyword = root.TryGetProperty("keyword", out var keywordElement) && keywordElement.ValueKind == JsonValueKind.String
            ? keywordElement.GetString()
            : null;
        var confidence = root.TryGetProperty("confidence", out var confElement) && confElement.ValueKind == JsonValueKind.Number
            ? confElement.GetDouble()
            : hit ? 1.0 : 0;
        return new WakeHit(hit ? keyword : null, 0, confidence);
    }

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

    private static string Truncate(string body) =>
        body.Length <= ErrorBodyLimit ? body : body[..ErrorBodyLimit] + "…";
}
