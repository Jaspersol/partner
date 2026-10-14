using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Partner.Voice.Asr;

/// <summary>
/// 本地 ASR HTTP 客户端（<see cref="AsrProtocols.SenseVoice"/>）：
/// <c>POST {BaseUrl}/recognize</c>，请求体是裸的小端 float32 PCM（16 kHz，本地传输不走 WAV 封装），
/// 响应是 <c>{ "text": … }</c>。
/// <para>
/// 与 <see cref="OpenAiAsrClient"/> 的差别是服务端即本地进程：一次拷贝都嫌多，直接扔采样数组；
/// 且引擎端无法给出 SSE，流式退化为一次性全文（同 OpenAI 客户端的处理）。
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
/// 唤醒门检查器：宿主端把一段采集帧交给引擎跑 KWS，答案一次一个。
/// <para>
/// 麦克风在宿主端（Partner），引擎不抢音频源：<see cref="VoiceInputHalf"/> 每次 tick 把最近
/// 的采集块交给 <see cref="IWakeChecker"/>，由实现转发给引擎的 <c>POST /wake/infer</c>。
/// 实现是 HTTP（<see cref="LocalWakeClient"/>），测试是脚本钩子。
/// </para>
/// </summary>
public interface IWakeChecker
{
    /// <summary>喂一块采集，问一次。关键词非空 = 本块内命中。</summary>
    Task<LocalWakeClient.WakeHit> PollAsync(float[] frame, CancellationToken ct = default);
}

/// <summary>
/// 唤醒词检测的 HTTP 客户端（本地 <see cref="AsrProtocols.SenseVoice"/> 引擎的伴生接口）：
/// 每个 <see cref="IWakeChecker.PollAsync"/> 把采集帧 <c>POST /wake/infer</c>；命中返回
/// <c>{ "hit": true, "keyword": …, "confidence": … }</c>，未命中 <c>{ "hit": false }</c>。
/// </summary>
public sealed class LocalWakeClient : IWakeChecker, IDisposable
{
    private const int ErrorBodyLimit = 512;

    /// <summary>一次命中或一次空探。</summary>
    public sealed record WakeHit(string? Keyword, long Seq, double Confidence)
    {
        /// <summary>keyword 空（引擎端匿名命中）也算命中：hit 位是权威，不是名字。</summary>
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
