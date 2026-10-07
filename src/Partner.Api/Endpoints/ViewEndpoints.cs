using Partner.Core.Agent.Llm;
using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Api.Voice;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Media;
using Partner.Core.Queue;
using Partner.Core.Sessions;
using Partner.Core.Agent.Skills;

namespace Partner.Api.Endpoints;

/// <summary>
/// The four views of design doc §2.10 plus the projection rebuild that Phase 7 requires. Every endpoint is a
/// thin projection over the stores — no view state lives here, which is why deleting the UI loses nothing.
/// </summary>
public static class ViewEndpoints
{
    public static void Map(WebApplication app, ApiContext ctx, string workspace)
    {
        MapInput(app);
        MapMedia(app);
        DesktopEndpoints.Map(app);
        MapSession(app);
        MapQueue(app);
        MapMemory(app);
        MapSettings(app);
        MapSkills(app, ctx, workspace);
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
            catch (Exception error) when (
                error is HttpRequestException
                    or InvalidOperationException
                    or TaskCanceledException
                    or ModelCallFailure)
            {
                // The reason travels: "the endpoint says 404" and "the endpoint is unreachable" need different
                // fixes, and an empty dropdown would say neither. ModelCallFailure carries the same answer in a
                // better shape, and is caught here for the same reason — a probe that 500s teaches nothing.
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
                return Results.BadRequest(new { error = $"未知的附件：{string.Join(", ", unknown)}" });
            }

            // Blank means "the message did not choose", which leaves the configured default in place. A named
            // gear must exist in the list, so the menu can never offer something the runner would reject.
            var gear = ReasoningEfforts.Normalize(request.ReasoningEffort);
            if (gear.Length > 0)
            {
                var known = ctx.Settings.Load().Model.ReasoningGears;
                if (!known.Contains(gear, StringComparer.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        error = $"未知的思考挡位 '{gear}'；可用：{string.Join("、", known)}",
                    });
                }
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
                ReasoningEffort = gear,
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

    // ---- attachments (图片与文档) -------------------------------------------------------------------
    private static void MapMedia(WebApplication app)
    {
        // The bytes come in as the raw body with the type in the header: no multipart parsing, and the UI can
        // send a File straight through. The name travels in a header rather than the body, so a File is still a
        // File and the body stays a byte range.
        app.MapPost("/api/media", async (HttpRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var contentType = (request.ContentType ?? string.Empty).Split(';')[0].Trim();
            // The client percent-encodes: a header can only carry ASCII, and a Chinese file name is the norm
            // here. A name that is not valid encoding is simply not a usable name — the store falls back to one.
            var encodedName = request.Headers["X-File-Name"].ToString();
            var fileName = encodedName.Contains('%')
                ? Uri.UnescapeDataString(encodedName)
                : encodedName;

            try
            {
                var mediaId = await ctx.Media
                    .SaveAsync(contentType, fileName, request.Body, ct)
                    .ConfigureAwait(false);
                var attachment = ctx.Media.TryRead(mediaId)!;

                return Results.Ok(new
                {
                    mediaId,
                    contentType = attachment.ContentType,
                    // The name and the path are what the model is told; without them an attachment is an id it
                    // cannot act on, and the message body would have to invent a location.
                    fileName = attachment.FileName,
                    path = attachment.LocalPath,
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
                : Results.NotFound(new { error = $"未知的附件 '{mediaId}'" }));

        // What this store will hold. A client that has to guess would rather be refused up front than after the
        // upload, and the browser's file picker needs the list to filter on.
        app.MapGet("/api/media/types", () => Results.Ok(new { types = FileMediaStore.Types }));
    }

    public sealed record InputRequest(
        string? Source,
        string? Payload,
        string? IdempotencyKey,
        IReadOnlyList<string>? Media = null,

        /// <summary>
        /// 这一条消息选的思考挡位。缺省 = 用设置里的默认挡位。
        /// <para>
        /// 校验的是「在清单里」，不是「在内置词表里」：清单是用户自己维护的，删掉一个词只会让它从菜单消失，
        /// 不会让服务端拒绝它。
        /// </para>
        /// </summary>
        string? ReasoningEffort = null);

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

                    // A tool call and its result are two events; the meta is what lets the UI pair them into
                    // one card (id/name/args/ok) instead of showing two unrelated rows.
                    meta = i.Meta,

                    // Only ever set on a live row; a logged Thinking event carries its text in the body like
                    // every other event, and inventing a second place for it would mean two sources of truth.
                    reasoning = (string?)null,
                    reasoningSince = (string?)null,
                })
                .ToList();

            // The reply being written right now rides along as one extra row. It is not in the log — a
            // half-written answer is not an event of record — so it carries seq 0 and disappears the moment
            // the real Assistant event lands. The view is already a mix of log and runtime (context below is
            // measured, not stored), so this is the same contract, not a new one.
            //
            // Reasoning rides on the same row rather than as a second one: a turn that is thinking has not yet
            // started answering, and a view that showed two live rows would be claiming two things are moving.
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
                    meta = (IReadOnlyDictionary<string, string>?)null,

                    // What the model is thinking right now. The row's own `at` is when the answer started,
                    // and ReasoningSince is when the thinking did — null means the thinking is over and only
                    // the answer is coming, which is the difference a reader can see.
                    reasoning = live.Reasoning,
                    reasoningSince = live.ReasoningSince?.ToString("O"),
                });
            }

            // Reasoning being streamed is not in the log yet either, so it gets the same treatment as the
            // answer: a row of its own, with seq 0, which the finished Thinking event replaces. While the model
            // is thinking there is no answer text yet, so this is the only live row there is.
            if (consumer.LiveAssistant is { Reasoning: { } thinking, ReasoningSince: { } since })
            {
                items.Add(new
                {
                    seq = 0L,

                    // `at` **is** the start of the thinking here, so the same field a client already reads off
                    // every row carries the clock. One source, not a second one that can disagree with it.
                    at = since,
                    kind = "ThinkingStreaming",
                    source = "Text",
                    body = thinking,
                    originalBody = (string?)null,
                    revisionCount = 0,
                    streaming = true,
                    meta = (IReadOnlyDictionary<string, string>?)null,
                    reasoning = (string?)null,
                    reasoningSince = (string?)null,
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

            // What was remembered from the deleted event goes with it. A record left behind would still be
            // recalled, and injected into a prompt weeks later, to a conversation where it was never said.
            var remembered = await MemoryDeletion
                .CascadeAsync(ctx.Memory, seq, seq, Actor.Human, ct)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                removedFrom = seq,
                removedTo = seq,
                purgedEvents = purged.RemovedEvents,
                purgedRevisions = purged.RemovedRevisions,
                purgedMemories = remembered,
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

            // Same cascade as a single delete, over the whole truncated span: a retraction that emptied the
            // conversation must not leave its memories behind to be recalled later.
            var remembered = await MemoryDeletion
                .CascadeAsync(ctx.Memory, seq, toSeq, Actor.Human, ct)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                removedFrom = seq,
                removedTo = toSeq,
                purgedEvents = purged.RemovedEvents,
                purgedRevisions = purged.RemovedRevisions,
                purgedMemories = remembered,
                eventSeq = purged.TombstoneSeq,
            });
        });
    }

    /// <summary>
    /// Whether the desktop section changed in a way the running consumer cannot absorb: the tool table and the
    /// window policy are built at start, the approval gear is read per call.
    /// </summary>
    private static bool DesktopNeedsRestart(DesktopSettings before, DesktopSettings after) =>
        before.Enabled != after.Enabled
        || !before.AllowedWindowTitles.SequenceEqual(after.AllowedWindowTitles, StringComparer.Ordinal);

    /// <summary>
    /// Whether the MCP section changed in a way the running consumer cannot absorb.
    /// <para>
    /// The allow list is deliberately excluded: governance reads it live, like the desktop approval gear, so
    /// changing it must not kill the servers and re-run every handshake. The server list and the on/off switch
    /// do restart, because they decide <em>which processes exist</em> — and a tool table is built at start.
    /// </para>
    /// </summary>
    private static bool McpNeedsRestart(McpSettings before, McpSettings after) =>
        before.Enabled != after.Enabled
        || !before.Servers.SequenceEqual(after.Servers);

    /// <summary>Only events the caller can currently see may be removed; removing a hidden seq is a mistake.</summary>
    private static async Task<bool> ExistsAsync(ApiContext ctx, long seq, CancellationToken ct)
    {
        var view = await ctx.Session.ProjectAsync(ct).ConfigureAwait(false);
        return view.Items.Any(item => item.Seq == seq);
    }

    // ---- queue view (查看 + 取消) ------------------------------------------------------------------
    private static void MapQueue(WebApplication app)
    {
        // No await: the answer is already in memory, which is the point.
        app.MapGet("/api/queue", (ApiContext ctx) =>
        {
            // From memory, not from the log. The log records every transition of every item this installation
            // has ever accepted; the queue is the items still in play. Reading the log here meant a full file
            // read — under the queue lock — every five seconds, growing forever, to hand back a list the queue
            // already held.
            var latest = ctx.Queue.ActiveItems()
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
                    // The gear a sender chose, so a stalled or failed turn can be traced back to the
                    // request that asked for it. Null means the message did not choose one.
                    reasoningEffort = string.IsNullOrEmpty(i.ReasoningEffort) ? null : i.ReasoningEffort,
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


        // The full list, including what has been retired. A memory view that hides invalidated records cannot
        // be "fully visible": the interesting question is usually "what replaced this", and that answer lives
        // on the row that was superseded.
        app.MapGet("/api/memory/all", async (ApiContext ctx, CancellationToken ct) =>
        {
            var records = await ctx.Memory.ListAsync(new MemoryQuery(IncludeInvalidated: true, Limit: 1000), ct)
                .ConfigureAwait(false);

            return Results.Ok(records.Select(r => new
            {
                recordId = r.RecordId,
                tier = r.Tier.ToString(),
                scope = r.Scope.ToString(),
                body = r.Body,
                createdAt = r.CreatedAt,
                lastRecalledAt = r.LastRecalledAt,
                validTo = r.ValidTo,
                supersededBy = r.SupersededBy,
                editedBy = r.EditedBy?.ToString(),
                importance = r.Importance,
            }));
        });

        // Tier is editable, because "this is a fact I rely on" and "this is something I once guessed" are
        // judgements only the person can make, and the whole demotion rule works off them.
        app.MapPost("/api/memory/{recordId}/tier", async (string recordId, TierRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            if (!Enum.TryParse<MemoryTier>(request.Tier, ignoreCase: true, out var tier))
            {
                return Results.BadRequest(new
                {
                    error = $"tier must be one of {string.Join(", ", Enum.GetNames<MemoryTier>())}",
                });
            }

            var changed = await ctx.Memory.SetTierAsync(recordId, tier, ct).ConfigureAwait(false);
            return changed
                ? Results.Ok(new { recordId, tier = tier.ToString() })
                : Results.NotFound(new { error = $"未知的记忆 '{recordId}'" });
        });

        // Retention is the other judgement only a person can make, and the one the clock cannot make for them:
        // it is the promise that demotion and trimming will not break.
        //
        // It deliberately does not touch the tier. Tier is about trust, retention is about time, and neither
        // implies the other — pinning retention to a single tier would have left Procedural (a way of doing
        // something, rather than a fact) unprotectable.
        app.MapPost("/api/memory/{recordId}/scope", async (string recordId, ScopeRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            if (!Enum.TryParse<MemoryScope>(request.Scope, ignoreCase: true, out var scope))
            {
                return Results.BadRequest(new
                {
                    error = $"scope must be one of {string.Join(", ", Enum.GetNames<MemoryScope>())}",
                });
            }

            var changed = await ctx.Memory.SetScopeAsync(recordId, scope, ct).ConfigureAwait(false);
            return changed
                ? Results.Ok(new { recordId, scope = scope.ToString() })
                : Results.NotFound(new { error = $"未知的记忆 '{recordId}'" });
        });

        // Writing a memory by hand. Until this existed the store had a write path (the distillation hook) and no
        // way for a person to add one, so the only records a reader could correct were the ones the distiller had
        // happened to keep.
        app.MapPost("/api/memory", async (CreateMemoryRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var body = (request.Body ?? string.Empty).Trim();

            if (body.Length < ConversationDistiller.MinBodyLength)
            {
                return Results.BadRequest(new
                {
                    error = $"正文至少 {ConversationDistiller.MinBodyLength} 个字",
                });
            }

            if (body.Length > ConversationDistiller.MaxBodyLength)
            {
                return Results.BadRequest(new
                {
                    error = $"正文最长 {ConversationDistiller.MaxBodyLength} 个字",
                });
            }

            var tier = Enum.TryParse<MemoryTier>(request.Tier, ignoreCase: true, out var parsed)
                ? parsed
                : MemoryTier.Semantic;

            // Semantic, not Working: something a person typed is not a note waiting to be corroborated, and
            // Working carries a TTL that would make the record they just wrote disappear on its own.
            var scope = Enum.TryParse<MemoryScope>(request.Scope, ignoreCase: true, out var parsedScope)
                ? parsedScope
                : MemoryScope.Recall;

            // No SourceEventSeq, so the distillation hook's "have I distilled this turn already" check cannot
            // collide with it, and a prefix of its own so the two id spaces cannot overlap.
            var recordId = $"m-manual-{Guid.NewGuid().ToString("n")[..8]}";

            await ctx.Memory.WriteAsync(
                new MemoryRecord
                {
                    RecordId = recordId,
                    Tier = tier,
                    Scope = scope,
                    Body = body,
                    CreatedAt = DateTimeOffset.UtcNow,
                    // Higher than a distilled note (0.5) because someone wrote this down on purpose.
                    Importance = 0.7,
                    Veracity = 1.0,
                    EditedBy = Actor.Human,
                },
                ct).ConfigureAwait(false);

            return Results.Ok(new { recordId, tier = tier.ToString(), scope = scope.ToString() });
        });

        app.MapPost("/api/memory/recall", async (RecallRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var hits = await ctx.Memory.RecallAsync(request.Query ?? string.Empty, request.Limit, ct).ConfigureAwait(false);

            // Tier and scope ride along because a hit *is* one of the records the list already shows. Without
            // them the list can only be a plain array of matches, and two things a person does with them
            // stop working: filtering the results the way they filtered the list, and telling a hit apart
            // from an anonymous one when the two are side by side.
            return Results.Ok(hits.Select(h => new
            {
                recordId = h.RecordId,
                body = h.Body,
                total = h.Score.Total,
                reasons = h.Reasons,
                tier = h.Tier.ToString(),
                scope = h.Scope.ToString(),
                // Raw, like `createdAt` on /api/memory/all: the two fields have the same name, so they have to
                // carry the same shape. System.Text.Json writes it as ISO 8601 with an offset.
                createdAt = h.CreatedAt,
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

    /// <summary>Body for a tier change: which tier the person is putting this record in.</summary>
    public sealed record TierRequest(string? Tier);

    /// <summary>Body for a retention change: whether this record is allowed to be forgotten.</summary>
    public sealed record ScopeRequest(string? Scope);

    /// <summary>
    /// Body for a hand-written memory. Tier and scope are optional and default to what a person writing one
    /// down would mean: an established fact, subject to the ordinary forgetting rules.
    /// </summary>
    public sealed record CreateMemoryRequest(string? Body, string? Tier, string? Scope);

    /// <summary>
    /// What skills the workspace holds, and which are switched off.
    /// <para>
    /// Read-only on purpose: a skill is a <c>SKILL.md</c> in the workspace, and writing one is a job for
    /// whatever produced it. What belongs here is the judgement that is this installation's — whether a
    /// given skill is used — which is why the disable list is in settings and the content is not here.
    /// </para>
    /// </summary>
    private static void MapSkills(WebApplication app, ApiContext ctx, string workspace)
    {
        app.MapGet("/api/skills", () =>
        {
            var disabled = ctx.Settings.Load().Skills.Disabled.ToHashSet(StringComparer.Ordinal);
            var loaded = new SkillLoader().Load(workspace);

            return Results.Ok(new
            {
                skills = loaded.Skills.Select(skill => new
                {
                    skillId = skill.SkillId,
                    name = skill.Name,
                    description = skill.Description,
                    enabled = !disabled.Contains(skill.SkillId),

                    // Carried so the view can show what a skill says without opening the file. A skill list
                    // that only shows names cannot be judged on whether to switch it on.
                    instructions = skill.Instructions,
                }),
                warnings = loaded.Warnings,
            });
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
            //
            // The desktop section is the exception among those: it decides which tools are registered and with
            // which window policy, and both are built when the consumer starts. The approval gear is *not* part
            // of that — governance reads it live — so changing only the gear does not restart anything. MCP is
            // the same shape twice over: the server list decides which processes exist, and the allow list
            // does not, so only the former restarts.
            if (ModelConfig.Differs(before.Model, stored.Model)
                || DesktopNeedsRestart(before.Desktop, stored.Desktop)
                || McpNeedsRestart(before.Mcp, stored.Mcp))
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
