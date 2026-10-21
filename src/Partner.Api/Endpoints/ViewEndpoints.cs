using Partner.Core.Agent.Llm;
using Partner.Api.Agents;
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
/// The four views plus the projection rebuild. Every endpoint is a thin projection over the stores — no view
/// state lives here.
/// </summary>
public static class ViewEndpoints
{
    public static void Map(WebApplication app, ApiContext ctx, string workspace)
    {
        MapInput(app);
        MapMedia(app);
        VoiceTestEndpoints.Map(app, ctx);
        VoiceReferenceEndpoints.Map(app, workspace);
        DesktopEndpoints.Map(app);
        MapSession(app);
        MapQueue(app);
        MapMemory(app);
        MapSettings(app);
        WebEndpoints.Map(app);
        AgentEndpoints.Map(app);
        ScheduleEndpoints.Map(app);
        MapSkills(app, ctx, workspace);
        SkillEndpoints.Map(app, ctx, workspace);
        MapProjections(app);
        MapDiscovery(app);
    }

    private static void MapDiscovery(WebApplication app)
    {
        app.MapGet("/api/voice/devices", (VoiceHost host) => Results.Ok(new
        {
            inputs = host.InputDevices,
            outputs = host.OutputDevices,
        }));

        // Proxied rather than called from the browser: the model endpoint will not allow the UI's origin,
        // and the API key must not reach the webview. Only the stored key is used, never a draft.
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
                // fixes, and an empty dropdown would say neither.
                return Results.Ok(new { baseUrl, models = Array.Empty<string>(), error = error.Message });
            }
        });
    }

    public sealed record ModelProbeRequest(string? BaseUrl);

    private static void MapInput(WebApplication app)
    {
        app.MapPost("/api/input", async (InputRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Payload))
            {
                return Results.BadRequest(new { error = "payload must not be empty" });
            }

            // Agent is the runtime talking to itself (a sub-agent's report); a client that could send it could
            // write a sub-agent's words, so it is not an accepted source.
            if (!Enum.TryParse<InputSource>(request.Source, ignoreCase: true, out var source) || source == InputSource.Agent)
            {
                return Results.BadRequest(new
                {
                    error = $"source must be one of {string.Join(", ", Enum.GetNames<InputSource>().Where(name => name != nameof(InputSource.Agent)))}",
                });
            }

            var mediaIds = request.Media ?? Array.Empty<string>();
            var unknown = mediaIds.Where(id => ctx.Media.TryRead(id) is null).ToList();
            if (unknown.Count > 0)
            {
                return Results.BadRequest(new { error = $"未知的附件：{string.Join(", ", unknown)}" });
            }

            // Blank means "the message did not choose", leaving the configured default. A named gear must exist
            // in the list, so the menu can never offer something the runner would reject.
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
            // call created the item. Scanning only happens when a key was supplied.
            var deduplicated = suppliedKey && await KeyExistsAsync(ctx, key, ct).ConfigureAwait(false);

            // A new message makes a pending withdrawal final. Done before the message is queued, so the purge is
            // finished before any turn can start that might recall withdrawn content.
            await ctx.Withdrawals.CommitAsync(ct).ConfigureAwait(false);

            var seq = await ctx.Queue.EnqueueAsync(new InputItem
            {
                ItemId = itemId,
                Source = source,
                Payload = request.Payload,
                MediaIds = mediaIds,
                IdempotencyKey = key,
                ReasoningEffort = gear,
            }, ct).ConfigureAwait(false);

            // The queue is the only place this lands: no consumer runs in this process, so the item waits until
            // one does. Saying so is more useful than implying it was executed.
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

    private static void MapMedia(WebApplication app)
    {
        // Bytes arrive as the raw body with the type in the header: no multipart parsing, and the UI can send a
        // File straight through. The name travels in a header so the body stays a byte range.
        app.MapPost("/api/media", async (HttpRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var contentType = (request.ContentType ?? string.Empty).Split(';')[0].Trim();
            // The client percent-encodes: headers carry ASCII only, and non-ASCII file names are common. An
            // invalid name is simply unusable — the store falls back to one.
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
                    // The name and path are what the model is told; without them an attachment is an id it
                    // cannot act on.
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

        // Served back so the composer can show what is attached without keeping a copy of the bytes.
        app.MapGet("/api/media/{mediaId}", (string mediaId, ApiContext ctx) =>
            ctx.Media.TryRead(mediaId) is { } attachment
                ? Results.Bytes(attachment.Bytes, attachment.ContentType)
                : Results.NotFound(new { error = $"未知的附件 '{mediaId}'" }));

        // What this store will hold: a client that would otherwise guess can be refused up front, and the
        // browser's file picker needs the list to filter on.
        app.MapGet("/api/media/types", () => Results.Ok(new { types = FileMediaStore.Types }));
    }

    public sealed record InputRequest(
        string? Source,
        string? Payload,
        string? IdempotencyKey,
        IReadOnlyList<string>? Media = null,

        /// <summary>
        /// The reasoning gear chosen for this message. Absent = the default gear from settings.
        /// <para>
        /// Validation checks membership in the configured list, not the built-in vocabulary: the list belongs to
        /// the user, so removing a word only hides it from the menu and does not make the server reject it.
        /// </para>
        /// </summary>
        string? ReasoningEffort = null);

    private static void MapSession(WebApplication app)
    {
        app.MapGet("/api/session", async (
            ApiContext ctx, ConsumerHost consumer, long? from, int? take,
            string? day, bool? open, int? offsetMinutes, CancellationToken ct) =>
        {
            var view = await ctx.Session.ProjectAsync(ct).ConfigureAwait(false);
            var pending = await ctx.Withdrawals.PendingAsync(ct).ConfigureAwait(false);

            // A sub-agent's report is the runtime talking to the main agent, not part of the person's
            // conversation: it stays in the log (the model reads it) and out of the view. The main agent's reply
            // is an ordinary assistant message and is shown.
            var shown = view.Items
                .Where(i => !(i.Kind == SessionEventKind.User && i.Source == InputSource.Agent))
                .ToList();

            // The day picker is a *view* over the one session: the log is not split, days are only a way to look
            // at it. The client passes its own UTC offset because "which day" is the reader's calendar. Without
            // `offsetMinutes` none of this runs.
            //
            // `day` picks the window ("latest" = the newest day that has events). A *closed* window is that day
            // alone; an *open* one starts at that day and runs to the end of the log, so a turn crossing midnight
            // keeps showing under the day the reader was looking at until they are idle.
            List<object>? days = null;
            string? resolvedDay = null;
            var windowFrom = from ?? 1;
            Func<SessionViewItem, bool> inWindow = i => true;
            var includeLive = true;

            if (offsetMinutes is { } minutes)
            {
                var offset = TimeSpan.FromMinutes(Math.Clamp(minutes, -14 * 60, 14 * 60));
                string DayOf(SessionViewItem i) => i.At.ToOffset(offset).ToString("yyyy-MM-dd");

                var groups = shown
                    .GroupBy(DayOf)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new { Day = g.Key, FirstSeq = g.Min(i => i.Seq), Total = g.Count() })
                    .ToList();
                days = groups.Select(g => (object)new { day = g.Day, count = g.Total }).ToList();

                if (day is not null && groups.Count > 0)
                {
                    var chosen = groups.FirstOrDefault(g => g.Day == day) ?? groups[^1];
                    resolvedDay = chosen.Day;
                    windowFrom = chosen.FirstSeq;
                    var isLatest = chosen.Day == groups[^1].Day;
                    if (open != true)
                    {
                        inWindow = i => DayOf(i) == chosen.Day;
                        // An earlier day is history: the reply being written belongs to the newest one.
                        includeLive = isLatest;
                    }
                }
            }

            var items = shown
                .Where(i => i.Seq >= windowFrom && inWindow(i))
                .Take(take ?? (day is null ? 200 : 5000))
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

                    // A tool call and its result are two events; the meta lets the UI pair them into one card
                    // (id/name/args/ok).
                    meta = i.Meta,

                    // Only ever set on a live row; a logged Thinking event carries its text in the body.
                    reasoning = (string?)null,
                    reasoningSince = (string?)null,
                })
                .ToList();

            // The reply being written now rides along as one extra row. It is not in the log — a half-written
            // answer is not an event of record — so it carries seq 0 and disappears when the real Assistant
            // event lands.
            //
            // Reasoning rides on the same row: a thinking turn has not started answering yet.
            if (includeLive && consumer.LiveAssistant is { } live)
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

                    // What the model is thinking now. The row's `at` is when the answer started, and
                    // ReasoningSince is when thinking did — null means thinking is over and only the answer is
                    // coming.
                    reasoning = live.Reasoning,
                    reasoningSince = live.ReasoningSince?.ToString("O"),
                });
            }

            // Streamed reasoning is not in the log yet either, so it gets the same treatment: a row of its own,
            // seq 0, which the finished Thinking event replaces. While thinking there is no answer text yet.
            if (includeLive && consumer.LiveAssistant is { Reasoning: { } thinking, ReasoningSince: { } since })
            {
                items.Add(new
                {
                    seq = 0L,

                    // `at` **is** the start of the thinking, so the field a client already reads off every row
                    // carries the clock.
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


            // The window's occupancy is measured the way a turn assembles it, so the UI's number is the one
            // that decides whether history is folded.
            var usage = await consumer.MeasureContextAsync(ctx.Settings.Load(), ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                lastSeq = view.LastSeq,
                items,
                days,
                day = resolvedDay,

                // What the view needs to offer the right actions: delete applies to the latest conversation
                // only, and a pending withdrawal is the one thing that can still be taken back.
                conversation = new
                {
                    latestFromSeq = WithdrawalService.LatestStart(view),
                    withdrawn = pending is null
                        ? null
                        : new
                        {
                            fromSeq = pending.FromSeq,
                            toSeq = pending.ToSeq,
                            conversations = pending.Conversations,
                            events = pending.Events,
                        },
                },
                // Whether a turn is in flight. The view uses it to decide how often to re-read itself.
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

        // Deleting applies to the latest conversation only and removes all of it. It is physical: the lines
        // leave the log, and one content-free retraction is left for the range so the seq space stays monotonic.
        //
        // Not earlier conversations, and not a single event out of the middle: removing history from behind newer
        // history leaves the rest of the conversation answering things that were never asked. Use withdrawal.
        app.MapPost("/api/session/{seq:long}/delete", async (
            long seq, ApiContext ctx, ConsumerHost consumer, CancellationToken ct) =>
        {
            if (Busy(ctx, consumer) is { } busy)
            {
                return busy;
            }

            if (await ctx.Withdrawals.PendingAsync(ct).ConfigureAwait(false) is not null)
            {
                return Results.Conflict(new { error = "有一次还没定下来的撤回：先取消撤回，或发送新消息让它生效，再删除" });
            }

            var view = await ctx.Session.ProjectAsync(ct).ConfigureAwait(false);
            if (view.Items.All(item => item.Seq != seq))
            {
                return Results.NotFound(new { error = $"#{seq} 不在当前视图里" });
            }

            if (WithdrawalService.LatestStart(view) is not { } latest)
            {
                return Results.NotFound(new { error = "没有可删除的对话" });
            }

            if (seq < latest)
            {
                return Results.Conflict(new { error = "只能删除最新一次对话；更早的对话请用撤回" });
            }

            // The turn's own markers go with it: they were written just before the message and belong to it.
            var fromSeq = await ctx.Withdrawals.StartOfTurnAsync(latest, ct).ConfigureAwait(false);
            var toSeq = ctx.Session.LastSeq;
            var purged = await ctx.Session.PurgeAsync(fromSeq, toSeq, ct).ConfigureAwait(false);

            // What was remembered from the deleted conversation goes with it. A record left behind would still be
            // recalled, and injected into a prompt weeks later, to a conversation where it was never said.
            var remembered = await MemoryDeletion
                .CascadeAsync(ctx.Memory, fromSeq, toSeq, Actor.Human, ct)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                removedFrom = fromSeq,
                removedTo = toSeq,
                purgedEvents = purged.RemovedEvents,
                purgedRevisions = purged.RemovedRevisions,
                purgedMemories = remembered,
                eventSeq = purged.TombstoneSeq,
            });
        });

        // Withdrawing hides the selected conversation and everything after it; the lines stay in the log and so
        // do their memories. That is what lets it be taken back — until the next message makes it final (see
        // WithdrawalService).
        app.MapPost("/api/session/{seq:long}/withdraw", async (
            long seq, ApiContext ctx, ConsumerHost consumer, CancellationToken ct) =>
        {
            // Hiding history under a turn still writing it would leave the turn's tail visible and headless.
            if (Busy(ctx, consumer) is { } busy)
            {
                return busy;
            }

            var pending = await ctx.Withdrawals.WithdrawAsync(seq, ct).ConfigureAwait(false);
            return pending is null
                ? Results.NotFound(new { error = $"#{seq} 不在当前视图里" })
                : Results.Ok(new
                {
                    withdrawnFrom = pending.FromSeq,
                    withdrawnTo = pending.ToSeq,
                    conversations = pending.Conversations,
                    events = pending.Events,
                });
        });

        app.MapPost("/api/session/withdrawal/cancel", async (ApiContext ctx, CancellationToken ct) =>
            await ctx.Withdrawals.CancelAsync(ct).ConfigureAwait(false)
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { error = "没有可取消的撤回（发送新消息后撤回就不能再取消了）" }));
    }

    /// <summary>A turn in flight makes removing history unsafe; null when nothing is running.</summary>
    private static IResult? Busy(ApiContext ctx, ConsumerHost consumer) =>
        consumer.LiveAssistant is not null || ctx.Queue.Snapshot().RunningItemId is not null
            ? Results.Conflict(new { error = "正在回复：等它结束（或先停止）再删除或撤回" })
            : consumer.Agents.HasActiveJobs
                // A running job reports back as a new message, which would land in a conversation whose history
                // was just taken away.
                ? Results.Conflict(new { error = "子 agent 还有任务在进行：等它汇报，或先取消任务，再删除或撤回" })
                : null;

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
    /// The allow list is deliberately excluded: governance reads it live, so changing it must not kill the
    /// servers and re-run every handshake. The server list and the on/off switch restart, because they decide
    /// <em>which processes exist</em> — and a tool table is built at start.
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

    private static void MapQueue(WebApplication app)
    {
        // No await: the answer is already in memory, which is the point.
        app.MapGet("/api/queue", (ApiContext ctx) =>
        {
            // From memory, not from the log: the log holds every transition ever recorded, while the queue holds
            // the items still in play.
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
                    // The gear the sender chose, so a stalled or failed turn can be traced to the request.
                    // Null means the message did not choose one.
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


        // The full list, including retired records: the interesting question is usually "what replaced this",
        // and that answer lives on the superseded row.
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

        // Tier is editable: "a fact I rely on" vs "something I once guessed" is a judgement only the person can
        // make, and the demotion rule works off it.
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

        // Retention is the other judgement only a person can make, and the promise that demotion and trimming
        // will not break.
        //
        // It deliberately does not touch the tier: tier is about trust, retention is about time, and neither
        // implies the other — pinning retention to a tier would leave Procedural unprotectable.
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

        // Writing a memory by hand, alongside the distiller's write path.
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

            // Absent means "the default a person means"; present-but-wrong is refused, like /tier and /scope.
            // Silently falling back to Semantic would store the record under an unrequested tier and answer 200.
            //
            // Semantic, not Working: something a person typed is not a note waiting to be corroborated, and
            // Working carries a TTL that would make the record disappear on its own.
            var tier = MemoryTier.Semantic;
            if (!string.IsNullOrWhiteSpace(request.Tier)
                && !Enum.TryParse<MemoryTier>(request.Tier, ignoreCase: true, out tier))
            {
                return Results.BadRequest(new
                {
                    error = $"tier must be one of {string.Join(", ", Enum.GetNames<MemoryTier>())}",
                });
            }

            var scope = MemoryScope.Recall;
            if (!string.IsNullOrWhiteSpace(request.Scope)
                && !Enum.TryParse<MemoryScope>(request.Scope, ignoreCase: true, out scope))
            {
                return Results.BadRequest(new
                {
                    error = $"scope must be one of {string.Join(", ", Enum.GetNames<MemoryScope>())}",
                });
            }

            // No SourceEventSeq on purpose. It keeps the distillation hook's "already distilled" check from
            // colliding with a hand-written record, and the id prefix keeps the two id spaces apart.
            //
            // Consequence: delete and withdraw promise that memories from removed turns go with them, and that
            // cascade (MemoryDeletion.CascadeAsync) walks source_event_seq. A hand-written memory therefore is
            // NOT removed with the conversation it was written next to — it belongs to the person. The UI says
            // so; a fuzzy body match would delete the wrong record and a back-link would restore the coupling.
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

            // Tier and scope ride along because a hit is one of the records the list already shows, so the
            // caller can filter results the same way and tell a hit apart from an anonymous match.
            return Results.Ok(hits.Select(h => new
            {
                recordId = h.RecordId,
                body = h.Body,
                total = h.Score.Total,
                reasons = h.Reasons,
                tier = h.Tier.ToString(),
                scope = h.Scope.ToString(),
                // Raw, like `createdAt` on /api/memory/all: same field name, same shape. System.Text.Json
                // writes it as ISO 8601 with an offset.
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
    /// A skill is a <c>SKILL.md</c> in the workspace. Reading is here; adding and removing one is in
    /// <see cref="SkillEndpoints"/>. Whether a skill is used is a separate judgement, which is why the disable
    /// list is in settings and the content is not.
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

                    // Carried so the view can show what a skill says without opening the file.
                    instructions = skill.Instructions,
                }),
                warnings = loaded.Warnings,
            });
        });
    }

    private static void MapSettings(WebApplication app)
    {
        app.MapGet("/api/settings", (ApiContext ctx) => Results.Ok(SettingsView.From(ctx.Settings.Load())));

        app.MapPut("/api/settings", async (
            RuntimeSettings incoming, ApiContext ctx, ConsumerHost consumer, VoiceHost voice,
            LocalEngineHost localEngines, CancellationToken ct) =>
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

            // The model section must take effect now rather than at the next restart. Other sections can change
            // without interrupting a turn in flight.
            //
            // The desktop section is the exception: it decides which tools are registered and with which window
            // policy, both built when the consumer starts. The approval gear is *not* part of that — governance
            // reads it live. MCP has the same shape: the server list decides which processes exist, the allow
            // list does not, so only the former restarts.
            if (ModelConfig.Differs(before.Model, stored.Model)
                || DesktopNeedsRestart(before.Desktop, stored.Desktop)
                || McpNeedsRestart(before.Mcp, stored.Mcp)
                || AgentRouting.ToolTableDiffers(before, stored))
            {
                var (config, reason) = ModelConfig.Resolve(stored);
                await consumer.ReconfigureAsync(config, reason).ConfigureAwait(false);
            }

            // The deny list is read when skills are loaded, so a change has to be loaded again to reach the next turn.
            if (!before.Skills.Equals(stored.Skills))
            {
                consumer.ReloadSkills();
            }

            // Local engines own the child processes behind the voice HTTP clients: a command/port/switch edit
            // converges them here (matches() leaves a correctly running one alone).
            if (before.Local != stored.Local)
            {
                await localEngines.ReconfigureAsync(stored.Local).ConfigureAwait(false);
            }

            // Shrinking the window leaves history that no longer fits, so it is folded here: waiting for the next
            // turn would send an over-long conversation to the model first.
            CompactionResult? compaction = null;
            if (stored.Model.ContextTokens < before.Model.ContextTokens)
            {
                // Measured under the new window, so the room left for history is what the assembler will allow
                // (it pays for the system block first).
                var usage = await consumer.MeasureContextAsync(stored, ct).ConfigureAwait(false);
                compaction = await consumer.CompactAsync(SessionCompactor.HistoryBudget(usage), ct).ConfigureAwait(false);
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
                local = localEngines.State,
                compaction,
            });
        });
    }

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
