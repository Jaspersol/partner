using System.Text;
using System.Text.Json.Nodes;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;
using Partner.Core.Memory;

namespace Partner.Api.Services;

/// <summary>
/// Turns buffered interactions into one sentence in the MAIN memory, on an idle or hard-ceiling timer. Uses an
/// injected <see cref="TimeProvider"/> so tests can advance time without waiting.
/// </summary>
internal sealed class ServiceSummarizer : IAsyncDisposable
{
    private readonly Func<ServiceSettings> _settings;
    private readonly ApiContext _ctx;
    private readonly HttpClient _http;
    private readonly PendingRounds _rounds;
    private readonly TimeProvider _clock;
    private readonly ITimer? _timer;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _disposed;

    public ServiceSummarizer(
        Func<ServiceSettings> settings,
        ApiContext ctx,
        HttpClient http,
        PendingRounds rounds,
        TimeProvider clock)
    {
        _settings = settings;
        _ctx = ctx;
        _http = http;
        _rounds = rounds;
        _clock = clock;
        _timer = clock.CreateTimer(
            static state => { _ = ((ServiceSummarizer)state!).TickAsync(); },
            this,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    private async Task TickAsync()
    {
        try
        {
            await FlushAsync(force: false, templateOnly: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A failed pass must not end the timer for good.
        }
    }

    /// <summary>
    /// Summarizes the pending window. <paramref name="force"/> is used on stop / disable / delete / manual flush.
    /// <paramref name="templateOnly"/> skips the model call entirely, for a shutdown that cannot wait.
    /// Returns the memory id written (or already present), or null when there was nothing to do.
    /// </summary>
    public async Task<string?> FlushAsync(bool force, bool templateOnly, CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The buffer, the record write and the commit use no cancellation token: a stop whose budget expires
            // mid-flush must not write the record and then abort before the commit. Only the model call is bounded.
            var view = await _rounds.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            // A flush interrupted after its intent was recorded. If the record made it to the store, the window is
            // written: commit it. If not, the intent is void and the window is summarized again below.
            if (view.Flushing is { } intent)
            {
                if (await _ctx.Memory.GetAsync(intent.RecordId, CancellationToken.None).ConfigureAwait(false) is not null)
                {
                    await _rounds.CommitAsync(intent.SnapshotSeq, intent.RecordId, CancellationToken.None)
                        .ConfigureAwait(false);
                    return intent.RecordId;
                }

                await _rounds.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                view = await _rounds.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (view.Rounds.Count == 0)
            {
                return null;
            }

            var settings = _settings();
            var now = _clock.GetUtcNow();

            // Decided on a read-only view: a tick that is not due must not freeze anything, or every round would
            // be split by the next reply.
            if (!force)
            {
                var idle = view.LastActivityAt is { } last ? now - last : TimeSpan.Zero;
                var waited = view.OldestAt is { } oldest ? now - oldest : TimeSpan.Zero;
                var due = idle >= TimeSpan.FromMinutes(settings.Summary.IntervalMinutes)
                    || waited >= TimeSpan.FromMinutes(settings.Summary.MaxWaitMinutes);
                if (!due)
                {
                    return null;
                }
            }

            var snapshot = await _rounds.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var written = false;
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var recordId = $"m-service-{settings.Id}-{snapshot.WindowSeq:x}";

                    // No intent pointed at this id, so an existing record belongs to another window: the counter
                    // was reset. Move past it instead of reading it as "already written".
                    if (await _ctx.Memory.GetAsync(recordId, CancellationToken.None).ConfigureAwait(false) is not null)
                    {
                        await _rounds.SkipWindowAsync(CancellationToken.None).ConfigureAwait(false);
                        snapshot = await _rounds.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }

                    string body;
                    if (templateOnly)
                    {
                        body = Fallback(settings, snapshot);
                    }
                    else
                    {
                        try
                        {
                            body = await BuildSummaryAsync(settings, snapshot, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // The caller's budget ran out (shutdown). The window is still written, with the
                            // template, because losing it silently is worse than a plainer sentence.
                            body = Fallback(settings, snapshot);
                        }
                    }

                    await _rounds.BeginFlushAsync(recordId, snapshot.SnapshotSeq, CancellationToken.None)
                        .ConfigureAwait(false);
                    await _ctx.Memory.WriteAsync(new MemoryRecord
                    {
                        RecordId = recordId,
                        Tier = MemoryTier.Episodic,
                        Scope = MemoryScope.Recall,
                        Body = body,
                        SourceEventSeq = null,
                        CreatedAt = now,
                        Importance = 0.5,
                        Veracity = 0.8,
                        Origin = MemoryOrigins.Service(settings.Id),
                    }, CancellationToken.None).ConfigureAwait(false);
                    written = true;

                    await _rounds.CommitAsync(snapshot.SnapshotSeq, recordId, CancellationToken.None)
                        .ConfigureAwait(false);
                    return recordId;
                }

                await _rounds.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch when (!written)
            {
                // Nothing reached the store: hand the window back so replies join their rounds again. Once the
                // record is written the intent stays, and the next flush commits it.
                await _rounds.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task<string> BuildSummaryAsync(ServiceSettings settings, RoundsView view, CancellationToken ct)
    {
        const string system =
            "你是一个对话摘要器。把按时间顺序排列的若干轮交互浓缩成一句话，严格使用下列句式：" +
            "多次请求时：「[对外服务:<名称>] 在做<主题>的过程中，调用方先要求<A>，再要求<B>；服务先<A′>，后<B′>。」" +
            "只有一次交互时：「[对外服务:<名称>] 调用方要求<A>；服务<A′>。」" +
            "标为（续）的轮次是上一窗口已经总结过的同一次请求的后续：只描述服务的后续动作，不要重复调用方的要求。" +
            "要求：只输出一句话；第三人称、带来源标签；不超过 120 字；不编造；不要输出任何解释。";

        var roundsText = string.Join("\n", view.Rounds.Select((round, i) =>
            round.ContinuesKey is null
                ? $"第{i + 1}轮：调用方：\"{round.User}\"；服务：\"{string.Join(" ", round.Assistants)}\"；"
                    + $"工具：{string.Join(",", round.Tools)}"
                : $"第{i + 1}轮（续）：调用方：\"{round.User}\"（上一窗口已总结同一请求）；服务：\"{string.Join(" ", round.Assistants)}\"；"
                    + $"工具：{string.Join(",", round.Tools)}"));

        var upstream = ServiceUpstream.Resolve(settings, _ctx.Settings.Load());
        if (string.IsNullOrWhiteSpace(upstream.BaseUrl))
        {
            return Fallback(settings, view);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var anthropic = LlmProtocols.Normalize(upstream.Protocol) == LlmProtocols.Anthropic;
            JsonObject payload;
            if (anthropic)
            {
                payload = new JsonObject
                {
                    ["model"] = upstream.Model,
                    ["system"] = system,
                    ["max_tokens"] = 200,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "user", ["content"] = roundsText },
                    },
                };
            }
            else
            {
                payload = new JsonObject
                {
                    ["model"] = upstream.Model,
                    ["max_tokens"] = 200,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = roundsText },
                    },
                };
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, ServiceUpstream.CompletionUrl(upstream))
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            ServiceUpstream.ApplyAuth(request, upstream);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Fallback(settings, view);
            }

            var text = ExtractSummary(json, anthropic);
            return string.IsNullOrWhiteSpace(text) ? Fallback(settings, view) : Validate(text, settings.Name);
        }
        catch
        {
            // A model failure or timeout must not lose the whole window: fall back to a template.
            return Fallback(settings, view);
        }
    }

    private static string Validate(string text, string name)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var label = $"[对外服务:{name}]";
        if (!single.StartsWith(label, StringComparison.Ordinal))
        {
            single = label + " " + single;
        }

        return single.Length > ConversationDistiller.MaxBodyLength
            ? single[..ConversationDistiller.MaxBodyLength]
            : single;
    }

    private static string Fallback(ServiceSettings settings, RoundsView view)
    {
        var parts = view.Rounds.Select(round =>
            $"调用方要求{Cut(round.User, 40)}；服务{Cut(string.Join(" ", round.Assistants), 40)}。");
        return Validate($"[对外服务:{settings.Name}] " + string.Concat(parts), settings.Name);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    private static string? ExtractSummary(string json, bool anthropic)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return null;
        }

        if (anthropic)
        {
            var builder = new StringBuilder();
            foreach (var block in (root["content"] as JsonArray) ?? new JsonArray())
            {
                if (block is JsonObject o && Str(o["type"]) == "text")
                {
                    builder.Append(Str(o["text"]) ?? string.Empty);
                }
            }
            return builder.ToString();
        }

        if ((root["choices"] as JsonArray)?[0] is JsonObject choice && choice["message"] is JsonObject message)
        {
            return Str(message["content"]);
        }

        return null;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await FlushAsync(force: true, templateOnly: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Shutdown must not fail over an unsent summary.
        }

        _flushGate.Dispose();
    }
}
