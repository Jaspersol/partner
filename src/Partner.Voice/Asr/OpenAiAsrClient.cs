using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Partner.Voice.Asr;

/// <summary>
/// 通用 ASR HTTP 客户端：OpenAI 标准 <c>POST {BaseUrl}/audio/transcriptions</c>，
/// WAV 以 multipart/form-data 上传，解析 <c>{ "text": "..." }</c> 响应。
/// <para>
/// 与 <see cref="HttpAsrClient"/> 的差别：不带 <c>language X&lt;asr_text&gt;</c> 元数据，
/// 不预填助手回合——whisper / funasr 等标准端点只认这三种字段：<c>file</c>、<c>model</c>、<c>language</c>。
/// 流式（<see cref="TranscribeStreamAsync"/>）端到端其实就是一次完整转写，所以非流式与流式行为一致。
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
        // 标准端点没有 SSE 流式转写：一次给全文，调用方当增量用。
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
