using Partner.Core.Agent.Llm;
using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Api.Voice;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Api.Endpoints;

/// <summary>
/// The four views of design doc §2.10 plus the projection rebuild that Phase 7 requires. Every endpoint is a
/// thin projection over the stores — no view state lives here, which is why deleting the UI loses nothing.
/// </summary>
public static class ViewEndpoints
{
    public static void Map(WebApplication app)
    {
        MapInput(app);
        MapMedia(app);
        MapSession(app);
        MapQueue(app);
        MapMemory(app);
        MapSettings(app);
        MapProjections(app);
        MapDiscovery(app);
    }

    // ---- discovery: what can be picked instead of typed ---------------------------------------------
    private static void MapDiscovery(WebApplication app)
    {
        app.MapGet("/api/voice/devices", (VoiceHost host) => Results.Ok(new
        {
            inputs = host.InputDevices,
            outputs = host.OutputDevices,
        }));

        // Proxied rather than called from the browser: the model endpoint will not allow the UI's origin,
        // and the API key must not travel to the webview. The key used is the stored one — a draft key is not
        // accepted here, because that would mean a second place secrets arrive from.
        app.MapPost("/api/models", async (
            ModelProbeRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var stored = ctx.Settings.Load();
            var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? stored.Model.BaseUrl : request.BaseUrl;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Results.BadRequest(new { error = "没有可探测的模型地址：先填 model.baseUrl" });
            }

            // The stored key only travels to the stored endpoint; probing somewhere else must not leak it.
            var apiKey = string.Equals(
                baseUrl.TrimEnd('/'), stored.Model.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? stored.Model.ApiKey
                : null;

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new HttpLlmClient(http, new HttpLlmOptions
            {
                BaseUrl = baseUrl.TrimEnd('/'),
                // The model name is irrelevant for a listing, but the type requires one.
                Model = stored.Model.Model ?? "listing",
                ApiKey = apiKey,
                Timeout = TimeSpan.FromSeconds(Math.Max(1, stored.Model.TimeoutSeconds)),
            });

            try
            {
                var models = await client.ListModelsAsync(ct).ConfigureAwait(false);
                return Results.Ok(new { baseUrl, models });
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // The reason travels: "the endpoint says 404" and "the endpoint is unreachable" need
                // different fixes, and an empty dropdown would say neither.
                return Results.Ok(new { baseUrl, models = Array.Empty<string>(), error = error.Message });
            }
        });
    }

    public sealed record ModelProbeRequest(string? BaseUrl);

    // ---- input delivery (design doc §2.3.1: the UI's single entry point) --------------------------
    private static void MapInput(WebApplication app)
    {
        app.MapPost("/api/input", async (InputRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Payload))
            {
                return Results.BadRequest(new { error = "payload must not be empty" });
            }

            if (!Enum.TryParse<InputSource>(request.Source, ignoreCase: true, out var source))
            {
                return Results.BadRequest(new
                {
                    error = $"source must be one of {string.Join(", ", Enum.GetNames<InputSource>())}",
                });
            }

            var mediaIds = request.Media ?? Array.Empty<string>();
            var unknown = mediaIds.Where(id => ctx.Media.TryRead(id) is null).ToList();
            if (unknown.Count > 0)
            {
                return Results.BadRequest(new { error = $"未知的附图：{string.Join(", ", unknown)}" });
            }

            var suppliedKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey);
            var itemId = "in-" + Guid.NewGuid().ToString("n");
            var key = suppliedKey ? request.IdempotencyKey! : $"ui:{source}:{itemId}";

            // A caller that supplies a key is asking for retry safety, so it is worth telling it whether this
            // call was the one that created the item. Scanning is only done when a key was supplied.
            var deduplicated = suppliedKey && await KeyExistsAsync(ctx, key, ct).ConfigureAwait(false);

            var seq = await ctx.Queue.EnqueueAsync(new InputItem
            {
                ItemId = itemId,
                Source = source,
                Payload = request.Payload,
                MediaIds = mediaIds,
                IdempotencyKey = key,
            }, ct).ConfigureAwait(false);

            // The queue is the only place this lands: no consumer runs in this process, so the item waits here
            // until one does. Saying so is more useful than implying it was executed.
            return Results.Ok(new
            {
                itemId,
                seq,
                status = InputItemStatus.Queued.ToString(),
                deduplicated,
            });
        });
    }

    private static async Task<bool> KeyExistsAsync(ApiContext ctx, string key, CancellationToken ct)
    {
        await foreach (var item in ctx.Queue.ReadLogAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(item.IdempotencyKey, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---- attachments (图片输入) --------------------------------------------------------------------
    private static void MapMedia(WebApplication app)
    {
        // The bytes come in as the raw body with the type in the header: no multipart parsing, and the UI can
        // send a File straight through.
        app.MapPost("/api/media", async (HttpRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var contentType = (request.ContentType ?? string.Empty).Split(';')[0].Trim();

            try
            {
                var mediaId = await ctx.Media.SaveAsync(contentType, request.Body, ct).ConfigureAwait(false);
                var attachment = ctx.Media.TryRead(mediaId)!;

                return Results.Ok(new
                {
                    mediaId,
                    contentType = attachment.ContentType,
                    bytes = attachment.Bytes.Length,
                });
            }
            catch (NotSupportedException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
            catch (InvalidOperationException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // Served back so the composer can show what is attached without keeping a copy of the bytes in the UI.
        app.MapGet("/api/media/{mediaId}", (string mediaId, ApiContext ctx) =>
            ctx.Media.TryRead(mediaId) is { } attachment
                ? Results.Bytes(attachment.Bytes, attachment.ContentType)
                : Results.NotFound(new { error = $"未知的附图 '{mediaId}'" }));
    }

    public sealed record InputRequest(
        string? Source,
        string? Payload,
        string? IdempotencyKey,
        IReadOnlyList<string>? Media = null);

    // ---- session view (查看 + 编辑 + 编辑历史) ----------------------------------------------------
    private static void MapSession(WebApplication app)
    {
        app.MapGet("/api/session", async (
            ApiContext ctx, ConsumerHost consumer, long? from, int? take, CancellationToken ct) =>
        {
            var view = await ctx.Session.ProjectAsync(ct).ConfigureAwait(false);
            var items = view.Items
                .Where(i => i.Seq >= (from ?? 1))
                .Take(take ?? 200)
                .Select(i => new
                {
                    seq = i.Seq,
                    at = i.At,
                    kind = i.Kind.ToString(),
                    source = i.Source.ToString(),
                    body = i.Body,
                    originalBody = i.OriginalBody,
                    revisionCount = i.RevisionCount,
                    streaming = false,
                })
                .ToList();

            // The reply being written right now rides along as one extra row. It is not in the log — a
            // half-written answer is not an event of record — so it carries seq 0 and disappears the moment
            // the real Assistant event lands. The view is already a mix of log and runtime (context below is
            // measured, not stored), so this is the same contract, not a new one.
            if (consumer.LiveAssistant is { } live)
            {
                items.Add(new
                {
                    seq = 0L,
                    at = DateTimeOffset.UtcNow,
                    kind = "AssistantStreaming",
                    source = "Text",
                    body = live.Text,
                    originalBody = (string?)null,
                    revisionCount = 0,
                    streaming = true,
                });
            }

            // The window's occupancy is measured the same way a turn would assemble it, so the number the UI
            // shows is the number that decides whether history has to be folded.
            var usage = await consumer.MeasureContextAsync(ctx.Settings.Load(), ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                lastSeq = view.LastSeq,
                items,
                // Whether a turn is in flight. The view uses it to decide how often to re-read itself: a
                // session that is being written to must show it while it happens, not five seconds later.
                busy = consumer.LiveAssistant is not null || ctx.Queue.Snapshot().RunningItemId is not null,
                context = new
                {
                    usedTokens = usage.UsedTokens,
                    budgetTokens = usage.BudgetTokens,
                    contextTokens = usage.ContextTokens,
                    maxTokens = usage.MaxTokens,
                    keptItems = usage.KeptItems,
                    foldedItems = usage.FoldedItems,
                    fraction = usage.Fraction,
                },
            });
        });

        app.MapPost("/api/session/{seq:long}/edit", async (
            long seq, EditRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "body must not be empty" });
            }

            // Editing appends a revision event; the original stays in the log and nothing re-runs.
            await ctx.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Edit,
                Source = InputSource.Text,
                Body = request.Body,
                EditedFromSeq = seq,
                Meta = new Dictionary<string, string> { ["actor"] = request.Actor ?? "human" },
            }, ct).ConfigureAwait(false);

            return Results.Ok(new { edited = seq });
        });

        app.MapGet("/api/session/{seq:long}/revisions", async (long seq, ApiContext ctx, CancellationToken ct) =>
        {
            var events = new List<SessionEvent>();
            await foreach (var e in ctx.Session.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (e.Kind == SessionEventKind.Edit && e.EditedFromSeq == seq)
                {
                    events.Add(e);
                }
            }

            return Results.Ok(events.Select(e => new { seq = e.Seq, at = e.At, body = e.Body, meta = e.Meta }));
        });

        // Both removals are physical: the lines leave the log, and one content-free retraction is left for the
        // range so the seq space stays monotonic. "Deleted" has to mean deleted — a hidden line is still a line
        // on disk, and the user asked for the log to lose it.
        app.MapPost("/api/session/{seq:long}/delete", async (long seq, ApiContext ctx, CancellationToken ct) =>
        {
            if (!await ExistsAsync(ctx, seq, ct).ConfigureAwait(false))
            {
                return Results.NotFound(new { error = $"#{seq} 不在当前视图里" });
            }

            var purged = await ctx.Session.PurgeAsync(seq, seq, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                removedFrom = seq,
                removedTo = seq,
                purgedEvents = purged.RemovedEvents,
                purgedRevisions = purged.RemovedRevisions,
                eventSeq = purged.TombstoneSeq,
            });
        });

        app.MapPost("/api/session/{seq:long}/retract", async (long seq, ApiContext ctx, CancellationToken ct) =>
        {
            if (!await ExistsAsync(ctx, seq, ct).ConfigureAwait(false))
            {
                return Results.NotFound(new { error = $"#{seq} 不在当前视图里" });
            }

            // The range is pinned here rather than at append time: events added afterwards stay visible, and
            // only what existed when the user confirmed gets removed.
            var toSeq = ctx.Session.LastSeq;
            var purged = await ctx.Session.PurgeAsync(seq, toSeq, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                removedFrom = seq,
                removedTo = toSeq,
                purgedEvents = purged.RemovedEvents,
                purgedRevisions = purged.RemovedRevisions,
                eventSeq = purged.TombstoneSeq,
            });
        });
    }

    /// <summary>Only events the caller can currently see may be removed; removing a hidden seq is a mistake.</summary>
    private static async Task<bool> ExistsAsync(ApiContext ctx, long seq, CancellationToken ct)
    {
        var view = await ctx.Session.ProjectAsync(ct).ConfigureAwait(false);
        return view.Items.Any(item => item.Seq == seq);
    }

    // ---- queue view (查看 + 取消) ------------------------------------------------------------------
    private static void MapQueue(WebApplication app)
    {
        app.MapGet("/api/queue", async (ApiContext ctx, CancellationToken ct) =>
        {
            var items = new List<InputItem>();
            await foreach (var item in ctx.Queue.ReadLogAsync(ct).ConfigureAwait(false))
            {
                items.Add(item);
            }

            // The log holds every transition; the view wants the latest state per item.
            var latest = items
                .GroupBy(i => i.ItemId, StringComparer.Ordinal)
                .Select(g => g.OrderBy(i => i.Seq).Last())
                .OrderBy(i => i.Seq)
                .Select(i => new
                {
                    itemId = i.ItemId,
                    seq = i.Seq,
                    source = i.Source.ToString(),
                    status = i.Status.ToString(),
                    enqueuedAt = i.EnqueuedAt,
                    startedAt = i.StartedAt,
                    finishedAt = i.FinishedAt,
                    attempt = i.Attempt,
                    error = i.Error,
                    payload = i.Payload,
                    mediaIds = i.MediaIds,
                });

            var state = ctx.Queue.Snapshot();
            return Results.Ok(new
            {
                headSeq = state.HeadSeq,
                runningItemId = state.RunningItemId,
                depth = state.Depth,
                items = latest,
            });
        });

        app.MapPost("/api/queue/{itemId}/cancel", async (string itemId, ApiContext ctx, CancellationToken ct) =>
        {
            var canceled = await ctx.Queue.CancelAsync(itemId, ct).ConfigureAwait(false);
            return canceled
                ? Results.Ok(new { canceled = itemId })
                : Results.NotFound(new { error = $"'{itemId}' is unknown or already finished" });
        });
    }

    // ---- memory view (查看 + 检索 + 编辑 + 删除) ----------------------------------------------------
    private static void MapMemory(WebApplication app)
    {
        app.MapGet("/api/memory", async (ApiContext ctx, string? tier, string? scope, int? take, CancellationToken ct) =>
        {
            var query = new MemoryQuery(
                Tier: Enum.TryParse<MemoryTier>(tier, ignoreCase: true, out var t) ? t : null,
                Scope: Enum.TryParse<MemoryScope>(scope, ignoreCase: true, out var s) ? s : null,
                Limit: take ?? 200);

            var records = await ctx.Memory.ListAsync(query, ct).ConfigureAwait(false);
            return Results.Ok(records.Select(r => new
            {
                recordId = r.RecordId,
                tier = r.Tier.ToString(),
                scope = r.Scope.ToString(),
                body = r.Body,
                createdAt = r.CreatedAt,
                validTo = r.ValidTo,
                supersededBy = r.SupersededBy,
                editedBy = r.EditedBy?.ToString(),
                importance = r.Importance,
            }));
        });

        app.MapPost("/api/memory/recall", async (RecallRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var hits = await ctx.Memory.RecallAsync(request.Query ?? string.Empty, request.Limit, ct).ConfigureAwait(false);
            return Results.Ok(hits.Select(h => new
            {
                recordId = h.RecordId,
                body = h.Body,
                total = h.Score.Total,
                reasons = h.Reasons,
            }));
        });

        app.MapPost("/api/memory/{recordId}/edit", async (
            string recordId, EditRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "body must not be empty" });
            }

            var actor = Enum.TryParse<Actor>(request.Actor, ignoreCase: true, out var a) ? a : Actor.Human;

            try
            {
                await ctx.Memory.ReviseAsync(recordId, request.Body, actor, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }

            return Results.Ok(new { edited = recordId, actor = actor.ToString() });
        });

        app.MapDelete("/api/memory/{recordId}", async (string recordId, ApiContext ctx, CancellationToken ct) =>
        {
            try
            {
                await ctx.Memory.SoftDeleteAsync(recordId, Actor.Human, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }

            // The four-step delete is completed by compaction; the endpoint does it so "deleted" means gone.
            var compacted = await ctx.Memory.CompactAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = recordId, compacted });
        });

        app.MapGet("/api/memory/{recordId}/revisions", async (string recordId, ApiContext ctx, CancellationToken ct) =>
        {
            var revisions = await ctx.Memory.RevisionsAsync(recordId, ct).ConfigureAwait(false);
            return Results.Ok(revisions.Select(r => new
            {
                at = r.At,
                before = r.Before,
                after = r.After,
                actor = r.Actor.ToString(),
            }));
        });
    }

    // ---- settings view ----------------------------------------------------------------------------
    private static void MapSettings(WebApplication app)
    {
        app.MapGet("/api/settings", (ApiContext ctx) => Results.Ok(SettingsView.From(ctx.Settings.Load())));

        app.MapPut("/api/settings", async (
            RuntimeSettings incoming, ApiContext ctx, ConsumerHost consumer, VoiceHost voice, CancellationToken ct) =>
        {
            var before = ctx.Settings.Load();

            RuntimeSettings stored;
            try
            {
                stored = ctx.Settings.Save(incoming);
            }
            catch (SettingsValidationException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message, errors = invalid.Errors });
            }

            // The model section is the one that must take effect now rather than at the next restart. Other
            // sections can change without interrupting a turn in flight.
            if (ModelConfig.Differs(before.Model, stored.Model))
            {
                var (config, reason) = ModelConfig.Resolve(stored);
                await consumer.ReconfigureAsync(config, reason).ConfigureAwait(false);
            }

            // Shrinking the window leaves history that no longer fits, so it is folded once, right here:
            // waiting for the next turn would send an over-long conversation to the model first.
            CompactionResult? compaction = null;
            if (stored.Model.ContextTokens < before.Model.ContextTokens)
            {
                var budget = ContextBudget.PromptBudget(stored.Model.ContextTokens, stored.Model.MaxTokens);
                compaction = await consumer.CompactAsync(budget, ct).ConfigureAwait(false);
            }

            // Voice halves are restarted only when their own sections changed: reopening a device mid-utterance
            // loses the utterance.
            if (before.Input != stored.Input || before.Output != stored.Output
                || before.Asr != stored.Asr || before.Tts != stored.Tts)
            {
                await voice.ReconfigureAsync(stored).ConfigureAwait(false);
            }

            return Results.Ok(new
            {
                settings = SettingsView.From(stored),
                consumer = consumer.State,
                voice = voice.State,
                compaction,
            });
        });
    }

    // ---- projection rebuild (Phase 7 acceptance) ----------------------------------------------------
    private static void MapProjections(WebApplication app)
    {
        app.MapPost("/api/projections/rebuild", async (ApiContext ctx, CancellationToken ct) =>
        {
            // Session: recompute the view from the append-only log.
            var view = await ctx.Session.ProjectAsync(ct).ConfigureAwait(false);

            // Memory: rebuild the FTS index from the content table.
            var indexed = await ctx.Memory.RebuildIndexAsync(ct).ConfigureAwait(false);

            // Queue: the projection is rebuilt by replaying its log on open, so report the replayed state.
            var queueItems = 0;
            await foreach (var _ in ctx.Queue.ReadLogAsync(ct).ConfigureAwait(false))
            {
                queueItems++;
            }

            return Results.Ok(new
            {
                sessionItems = view.Items.Count,
                sessionLastSeq = view.LastSeq,
                memoryIndexed = indexed,
                queueLogEntries = queueItems,
            });
        });
    }

    public sealed record EditRequest(string? Body, string? Actor);

    public sealed record RecallRequest(string? Query, int? Limit);
}
