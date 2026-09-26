using Partner.Api.Settings;
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
        MapSession(app);
        MapQueue(app);
        MapMemory(app);
        MapSettings(app);
        MapProjections(app);
    }

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

    public sealed record InputRequest(string? Source, string? Payload, string? IdempotencyKey);

    // ---- session view (查看 + 编辑 + 编辑历史) ----------------------------------------------------
    private static void MapSession(WebApplication app)
    {
        app.MapGet("/api/session", async (ApiContext ctx, long? from, int? take, CancellationToken ct) =>
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
                });

            return Results.Ok(new { lastSeq = view.LastSeq, items });
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
            RuntimeSettings incoming, ApiContext ctx, ConsumerHost consumer, CancellationToken ct) =>
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

            return Results.Ok(new { settings = SettingsView.From(stored), consumer = consumer.State });
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
