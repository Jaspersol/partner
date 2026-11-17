namespace Partner.Smoke;

internal static partial class Program
{
    private static async Task ApiChecksAsync(string root, TimeProvider clock)
    {
        var uiWorkspace = Path.Combine(root, "ui");
        var apiPort = FreePort();
        var uiToken = TokenAuth.Generate();
        var (apiApp, apiContext, apiConsumer, apiVoice, _, _) = await ApiHost.BuildAsync(uiWorkspace, apiPort, uiToken, clock);
        var apiRun = apiApp.RunAsync();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", uiToken);

        try
        {
            // Wait for the host to accept connections (this also proves the token is accepted).
            for (var i = 0; i < 200; i++)
            {
                try
                {
                    using var probe = await http.GetAsync("/api/health");
                    if (probe.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50);
                }
            }

            // Authentication is fail-closed: the middleware protects every endpoint, health included.
            using var anonymous = new HttpClient { BaseAddress = http.BaseAddress! };
            using var wrongToken = new HttpClient { BaseAddress = http.BaseAddress! };
            wrongToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

            var anonymousHealth = await anonymous.GetAsync("/api/health");
            var anonymousSession = await anonymous.GetAsync("/api/session");
            var wrongTokenSession = await wrongToken.GetAsync("/api/session");
            var authorizedSession = await http.GetAsync("/api/session");

            Check("ui: an unauthenticated request is rejected on every endpoint",
                anonymousHealth.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && anonymousSession.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                $"health={anonymousHealth.StatusCode} session={anonymousSession.StatusCode}");

            Check("ui: a wrong token is rejected and the right one is accepted",
                wrongTokenSession.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && authorizedSession.IsSuccessStatusCode,
                $"wrong={wrongTokenSession.StatusCode} right={authorizedSession.StatusCode}");

            // Seed a session with a user turn and a tool call, then edit the user turn.
            var userSeq = await apiContext.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = "turn on teh lamp",
            });
            await apiContext.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.ToolCall,
                Source = InputSource.Text,
                Body = "lamp.set",
            });

            var editResponse = await http.PostAsJsonAsync(
                $"/api/session/{userSeq}/edit",
                new { body = "turn on the lamp", actor = "human" });
            editResponse.EnsureSuccessStatusCode();

            var sessionView = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var editedSessionItem = sessionView.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("seq").GetInt64() == userSeq);

            var revisions = await http.GetFromJsonAsync<JsonElement>($"/api/session/{userSeq}/revisions");

            // "Editing re-runs nothing" is proved on the raw log: exactly one new event (the edit itself),
            // and the tool-call count is unchanged.
            var uiRawEvents = new List<SessionEvent>();
            await foreach (var e in apiContext.Session.ReadAllAsync())
            {
                uiRawEvents.Add(e);
            }

            var toolCalls = uiRawEvents.Count(e => e.Kind == SessionEventKind.ToolCall);
            var edits = uiRawEvents.Count(e => e.Kind == SessionEventKind.Edit);

            Check("ui: session view shows the edit, keeps the original, and re-runs nothing",
                editedSessionItem.GetProperty("body").GetString() == "turn on the lamp"
                && editedSessionItem.GetProperty("originalBody").GetString() == "turn on teh lamp"
                && editedSessionItem.GetProperty("revisionCount").GetInt32() == 1
                && revisions.GetArrayLength() == 1
                && uiRawEvents.Count == 3
                && toolCalls == 1
                && edits == 1,
                $"body={editedSessionItem.GetProperty("body").GetString()} revisions={revisions.GetArrayLength()} "
                + $"rawEvents={uiRawEvents.Count} toolCalls={toolCalls} edits={edits}");

            // Memory: write, edit (human), then delete.
            await apiContext.Memory.WriteAsync(new MemoryRecord
            {
                RecordId = "ui-mem-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Retain,
                Body = "the user prefers dark mode",
                Importance = 0.9,
            });

            var memEdit = await http.PostAsJsonAsync(
                "/api/memory/ui-mem-1/edit",
                new { body = "the user prefers dark mode everywhere", actor = "human" });
            memEdit.EnsureSuccessStatusCode();

            var memRevisions = await http.GetFromJsonAsync<JsonElement>("/api/memory/ui-mem-1/revisions");
            var recall = await http.PostAsJsonAsync("/api/memory/recall", new { query = "dark mode everywhere" });
            var recallBody = await recall.Content.ReadFromJsonAsync<JsonElement>();

            Check("ui: memory edit records the actor and the revision",
                memRevisions.GetArrayLength() == 1
                && memRevisions[0].GetProperty("actor").GetString() == "Human"
                && recallBody.GetArrayLength() >= 1
                && recallBody[0].GetProperty("reasons").GetArrayLength() > 0,
                $"revisions={memRevisions.GetArrayLength()} hits={recallBody.GetArrayLength()}");

            var delete = await http.DeleteAsync("/api/memory/ui-mem-1");
            delete.EnsureSuccessStatusCode();

            var afterDelete = await http.PostAsJsonAsync("/api/memory/recall", new { query = "dark mode everywhere" });
            var afterDeleteBody = await afterDelete.Content.ReadFromJsonAsync<JsonElement>();
            var memList = await http.GetFromJsonAsync<JsonElement>("/api/memory");

            Check("ui: memory delete removes it from search and from the list",
                afterDeleteBody.GetArrayLength() == 0 && memList.GetArrayLength() == 0,
                $"hits={afterDeleteBody.GetArrayLength()} listed={memList.GetArrayLength()}");

            // Projection rebuild must reproduce the same view, entry by entry.
            var beforeRebuild = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var rebuild = await http.PostAsync("/api/projections/rebuild", content: null);
            rebuild.EnsureSuccessStatusCode();
            var afterRebuild = await http.GetFromJsonAsync<JsonElement>("/api/session");

            Check("ui: rebuilding projections reproduces the same session view",
                beforeRebuild.GetProperty("items").ToString() == afterRebuild.GetProperty("items").ToString()
                && beforeRebuild.GetProperty("lastSeq").GetInt64() == afterRebuild.GetProperty("lastSeq").GetInt64(),
                $"before={beforeRebuild.GetProperty("items").GetArrayLength()} after={afterRebuild.GetProperty("items").GetArrayLength()}");

            // Settings round trip.
            var settings = await http.GetFromJsonAsync<JsonElement>("/api/settings");
            var putSettings = await http.PutAsJsonAsync("/api/settings", new
            {
                revision = settings.GetProperty("revision").GetInt64(),
                input = new { enabled = true, utteranceGapMs = 900 },
                output = new { enabled = true, muteDuringTts = true },
                asr = new { baseUrl = "http://127.0.0.1:45101", model = "smoke-asr", language = "Chinese" },
                tts = new { baseUrl = "http://127.0.0.1:45102", lang = "zh", chunkMs = 120 },
            });
            putSettings.EnsureSuccessStatusCode();
            var saved = await http.GetFromJsonAsync<JsonElement>("/api/settings");

            Check("ui: settings round-trip every section through the API",
                settings.GetProperty("input").GetProperty("utteranceGapMs").GetDouble() == 1200
                && saved.GetProperty("input").GetProperty("utteranceGapMs").GetDouble() == 900
                && saved.GetProperty("input").GetProperty("enabled").GetBoolean()
                && saved.GetProperty("output").GetProperty("muteDuringTts").GetBoolean()
                && saved.GetProperty("asr").GetProperty("model").GetString() == "smoke-asr"
                && saved.GetProperty("tts").GetProperty("chunkMs").GetInt32() == 120,

                // The memory section rides the same PUT: off is the shipped default, so this also asserts the
                // section arrives rather than being dropped by the view.
                $"gap={saved.GetProperty("input").GetProperty("utteranceGapMs").GetDouble()} "
                + $"asr={saved.GetProperty("asr").GetProperty("model").GetString()} "
                + $"tts={saved.GetProperty("tts").GetProperty("chunkMs").GetInt32()}");
            Check("memory: embeddings default off and the switch round-trips",
                settings.GetProperty("memory").GetProperty("embeddings").GetBoolean() == false
                && saved.GetProperty("memory").GetProperty("embeddings").GetBoolean() == false,
                $"default={settings.GetProperty("memory").GetProperty("embeddings").GetBoolean()}"
                + $" afterSave={saved.GetProperty("memory").GetProperty("embeddings").GetBoolean()}");

            // Queue view + cancel. The view is served from what is still in play rather than from the log, so a
            // cancelled item leaves the list instead of appearing in it as Canceled. The check asserts that
            // shape: Queued while waiting, absent once settled, with the depth following it down.
            var queuedSeq = await apiContext.Queue.EnqueueAsync(new InputItem
            {
                ItemId = "ui-queued",
                Source = InputSource.Text,
                Payload = "queued from the ui",
                IdempotencyKey = "text:ui:1",
            });
            var queueView = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var listedBefore = queueView.GetProperty("items").EnumerateArray()
                .FirstOrDefault(i => i.GetProperty("itemId").GetString() == "ui-queued");
            var beforeStatus = listedBefore.ValueKind == JsonValueKind.Object
                ? listedBefore.GetProperty("status").GetString()
                : "(not listed)";
            var cancel = await http.PostAsync("/api/queue/ui-queued/cancel", content: null);
            cancel.EnsureSuccessStatusCode();
            var queueAfter = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var stillListed = queueAfter.GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("itemId").GetString() == "ui-queued");

            Check("ui: queue view shows state and honours cancel",
                queuedSeq == 1
                && queueView.GetProperty("depth").GetInt32() == 1
                && beforeStatus == "Queued"
                && !stillListed
                && queueAfter.GetProperty("depth").GetInt32() == 0,
                $"seq={queuedSeq} depth={queueView.GetProperty("depth").GetInt32()} before={beforeStatus} "
                + $"stillListed={stillListed} depthAfter={queueAfter.GetProperty("depth").GetInt32()}");

            // The composer's entry point: it delivers to the queue and nothing executes it here.
            var sessionBefore = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var submitted = await http.PostAsJsonAsync("/api/input", new { source = "text", payload = "from the composer" });
            var submittedBody = await submitted.Content.ReadFromJsonAsync<JsonElement>();
            var queueAfterInput = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var sessionAfter = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var composerItem = queueAfterInput.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("itemId").GetString() == submittedBody.GetProperty("itemId").GetString());

            Check("ui: submitting from the composer queues work without executing it",
                submitted.IsSuccessStatusCode
                && submittedBody.GetProperty("status").GetString() == "Queued"
                && composerItem.GetProperty("source").GetString() == "Text"
                && sessionAfter.GetProperty("lastSeq").GetInt64() == sessionBefore.GetProperty("lastSeq").GetInt64(),
                $"status={submittedBody.GetProperty("status").GetString()} source={composerItem.GetProperty("source").GetString()} "
                + $"lastSeq {sessionBefore.GetProperty("lastSeq").GetInt64()}->{sessionAfter.GetProperty("lastSeq").GetInt64()}");
        }
        finally
        {
            await apiApp.StopAsync();
            await apiRun;
            await apiVoice.DisposeAsync();
            await apiConsumer.DisposeAsync();
            await apiContext.DisposeAsync();
        }

        {
            // Without a model the consumer must not run at all, and it must say so: work stays queued and the
            // state is visible rather than silently stuck.
            var disabledWorkspace = Path.Combine(root, "consumer-off");
            var disabledPort = FreePort();
            var disabledToken = TokenAuth.Generate();
            var (offApp, offContext, offConsumer, offVoice, _, _) = await ApiHost.BuildAsync(disabledWorkspace, disabledPort, disabledToken, clock);
            var offRun = offApp.RunAsync();
            using var offHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{disabledPort}") };
            offHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", disabledToken);

            try
            {
                await WaitForHostAsync(offHttp);
                var offState = await offHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                await offHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "orphan work" });
                var offQueue = await offHttp.GetFromJsonAsync<JsonElement>("/api/queue");

                Check("consumer: without a model it is disabled, says why, and leaves work queued",
                    !offState.GetProperty("running").GetBoolean()
                    && offState.GetProperty("reason").GetString()!.Contains("PARTNER_LLM", StringComparison.Ordinal)
                    && offQueue.GetProperty("depth").GetInt32() == 1
                    && offState.GetProperty("stats").GetProperty("handled").GetInt32() == 0,
                    $"running={offState.GetProperty("running").GetBoolean()} depth={offQueue.GetProperty("depth").GetInt32()}");

                // Shrinking the window with no model still saves: the endpoint reports that compaction is
                // deferred, rather than a 500 (or a silent loss) — the save itself must never break.
                var offBefore = await offHttp.GetFromJsonAsync<JsonElement>("/api/settings");
                if (!offBefore.TryGetProperty("model", out _))
                {
                    throw new InvalidOperationException("settings view lost the model section");
                }

                var offShrink = await offHttp.PutAsJsonAsync("/api/settings", new
                {
                    revision = offBefore.GetProperty("revision").GetInt64(),
                    model = new
                    {
                        // Record-replace semantics: both limits, and maxTokens must stay below the window.
                        contextTokens = 8192,
                        maxTokens = 2048,
                    },
                });

                var offCompaction = (await offShrink.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("compaction");
                Check("settings: shrinking the window without a model saves and reports the deferred compaction",
                    offShrink.IsSuccessStatusCode
                    && offCompaction.ValueKind == JsonValueKind.Object
                    && !offCompaction.GetProperty("compacted").GetBoolean()
                    && (offCompaction.GetProperty("reason").GetString() ?? string.Empty).Contains("压缩", StringComparison.Ordinal),
                    $"status={(int)offShrink.StatusCode} reason={offCompaction.GetProperty("reason").GetString()}");
            }
            finally
            {
                await offApp.StopAsync();
                await offRun;
                await offVoice.DisposeAsync();
                await offConsumer.DisposeAsync();
                await offContext.DisposeAsync();
            }

            // With a model, the same delivery becomes a turn that lands in the session.
            var onWorkspace = Path.Combine(root, "consumer-on");
            var onPort = FreePort();
            var onToken = TokenAuth.Generate();
            var onLlm = new ScriptedLlmClient(new[]
            {
                new LlmResponse("书房的灯已经打开了。", Array.Empty<LlmToolCall>(), "stop"),
            });
            var (onApp, onContext, onConsumer, onVoice, _, _) = await ApiHost.BuildAsync(onWorkspace, onPort, onToken, clock, onLlm);
            var onRun = onApp.RunAsync();
            using var onHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{onPort}") };
            onHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", onToken);

            try
            {
                await WaitForHostAsync(onHttp);
                var onState = await onHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                var delivered = await onHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "打开书房的灯" });
                var deliveredId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var settled = await WaitForQueueItemToSettleAsync(onHttp, deliveredId);

                var session = await onHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var entries = session.GetProperty("items").EnumerateArray()
                    .Select(item => (Kind: item.GetProperty("kind").GetString(), Body: item.GetProperty("body").GetString()))
                    .ToList();

                Check("consumer: with a model the delivered input becomes a turn in the session",
                    onState.GetProperty("running").GetBoolean()
                    && settled
                    && entries.Any(e => e.Kind == "User" && e.Body == "打开书房的灯")
                    && entries.Any(e => e.Kind == "Assistant" && e.Body == "书房的灯已经打开了。"),
                    $"running={onState.GetProperty("running").GetBoolean()} settled={settled} entries={entries.Count}");

                var afterState = await onHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                var queueAfter = await onHttp.GetFromJsonAsync<JsonElement>("/api/queue");
                Check("consumer: the queue drains and the loop counts what it handled",
                    queueAfter.GetProperty("depth").GetInt32() == 0
                    && afterState.GetProperty("stats").GetProperty("completed").GetInt32() == 1,
                    $"depth={queueAfter.GetProperty("depth").GetInt32()} completed={afterState.GetProperty("stats").GetProperty("completed").GetInt32()}");
            }
            finally
            {
                await onApp.StopAsync();
                await onRun;
                await onVoice.DisposeAsync();
                await onConsumer.DisposeAsync();
                await onContext.DisposeAsync();
            }

            // The production client over a real socket: a minimal server that speaks the OpenAI shape stands in
            // for a provider, so the whole chain (deliver → loop → HTTP → parse → session) is exercised for real.
            /// <summary>The host holds these files open, so a reader has to allow its writer back.</summary>
            static async Task<string> ReadSharedAsync(string path)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }

            var wireWorkspace = Path.Combine(root, "consumer-wire");
            var wirePort = FreePort();
            var wireToken = TokenAuth.Generate();
            var modelPort = FreePort();

            using var provider = new FakeChatProvider(modelPort, "通过真实 HTTP 返回。");
            var httpClient = new HttpLlmClient(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                new HttpLlmOptions { BaseUrl = $"http://127.0.0.1:{modelPort}/v1", Model = "smoke-model" });

            var (wireApp, wireContext, wireConsumer, wireVoice, _, _) = await ApiHost.BuildAsync(wireWorkspace, wirePort, wireToken, clock, httpClient);
            var wireRun = wireApp.RunAsync();
            using var wireHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{wirePort}") };
            wireHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wireToken);

            try
            {
                await WaitForHostAsync(wireHttp);
                var delivered = await wireHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "走一次真实 HTTP" });
                var deliveredId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var settled = await WaitForQueueItemToSettleAsync(wireHttp, deliveredId);

                var session = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var assistant = session.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() == "Assistant")
                    .Select(item => item.GetProperty("body").GetString())
                    .ToList();

                var wireQueue = await wireHttp.GetFromJsonAsync<JsonElement>("/api/queue");
                var stillQueued = wireQueue.GetProperty("items").EnumerateArray()
                    .Any(item => item.GetProperty("itemId").GetString() == deliveredId);

                Check("consumer: a real HTTP model call completes a turn end to end",
                    settled
                    && !stillQueued
                    && assistant.Count == 1
                    && assistant[0] == "通过真实 HTTP 返回。"
                    && provider.RequestPath?.Contains("/v1/chat/completions", StringComparison.Ordinal) == true
                    // No key configured means no Authorization header, rather than an empty one.
                    && provider.SawAuthorization is null,
                    $"settled={settled} depth={wireQueue.GetProperty("depth").GetInt32()} "
                    + $"requests=[{string.Join(" | ", provider.Requests)}]");

                // Streaming: the reply has to be readable while it is still being written. The provider releases
                // it two characters at a time with a pause between them, so a poll landing mid-turn sees a
                // partial answer that is longer than the one before it.
                //
                // A second attempt is allowed because a loaded machine can stall past the whole window; the
                // check is about the mechanism, and a broken one never passes either attempt. The deterministic
                // version of this lives in the API tests, which gate the model instead of racing it.
                var sawLive = false;
                var grew = false;
                var firstLive = string.Empty;

                for (var round = 0; round < 2 && !(sawLive && grew); round++)
                {
                    await wireHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "流式一轮" });

                    for (var attempt = 0; attempt < 600 && !(sawLive && grew); attempt++)
                    {
                        var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        var live = polled.GetProperty("items").EnumerateArray()
                            .FirstOrDefault(item => item.GetProperty("streaming").GetBoolean());

                        if (live.ValueKind == JsonValueKind.Object)
                        {
                            var body = live.GetProperty("body").GetString() ?? string.Empty;

                            if (!sawLive)
                            {
                                sawLive = true;
                                firstLive = body;
                            }
                            else if (body.Length > firstLive.Length)
                            {
                                grew = true;
                            }
                        }

                        await Task.Delay(10);
                    }

                    // Let the turn finish before deciding whether to try again.
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        if (!polled.GetProperty("busy").GetBoolean())
                        {
                            break;
                        }

                        await Task.Delay(25);
                    }
                }

                Check("session: the reply is visible while it is still being written",
                    sawLive && grew,
                    $"live={sawLive} grew={grew} first={firstLive}");

                // And the request that produced it really did ask for a stream, rather than getting a whole
                // answer that happened to arrive quickly.
                Check("session: the client asks the model to stream",
                    provider.LastRequestBody?.Contains("\"stream\":true", StringComparison.Ordinal) == true,
                    provider.LastRequestBody is null ? "(no body)" : provider.LastRequestBody[..Math.Min(120, provider.LastRequestBody.Length)]);

                // And when the turn ends, the live row is replaced by the event it stood in for.
                var liveGone = false;
                var finalAssistant = string.Empty;

                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    var streaming = polled.GetProperty("items").EnumerateArray()
                        .Any(item => item.GetProperty("streaming").GetBoolean());
                    var idle = !polled.GetProperty("busy").GetBoolean();

                    if (!streaming && idle)
                    {
                        liveGone = true;
                        finalAssistant = polled.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "Assistant")
                            .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                            .LastOrDefault() ?? string.Empty;
                        break;
                    }

                    await Task.Delay(25);
                }

                Check("session: the finished reply replaces the live row, whole",
                    liveGone && finalAssistant == "通过真实 HTTP 返回。",
                    $"gone={liveGone} body={finalAssistant}");

                // Tool rows: a turn that called a tool must not look like it did nothing. They reach the view
                // (the UI folds them), and the prompt builder ignores them — checked in Core, where the prompt
                // is built.
                await wireContext.Session.AppendAsync(new SessionEvent
                {
                    Kind = SessionEventKind.ToolCall,
                    Source = InputSource.Text,
                    Body = "clock",
                });

                await wireContext.Session.AppendAsync(new SessionEvent
                {
                    Kind = SessionEventKind.ToolResult,
                    Source = InputSource.Text,
                    Body = "{\"now\":\"12:00\"}",
                });

                var withTools = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var kinds = withTools.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("kind").GetString())
                    .ToList();

                Check("session: tool calls and results reach the view",
                    kinds.Contains("ToolCall") && kinds.Contains("ToolResult"),
                    string.Join(",", kinds.Distinct()));
                // Image input: the bytes reach the model inline, and the log keeps only the id. A 1×1 PNG is
                // enough — what is being checked is the shape of the request, not the pixels.
                using (var upload = new ByteArrayContent(Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")))
                {
                    upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    var uploaded = await wireHttp.PostAsync("/api/media", upload);
                    var mediaId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>())
                        .GetProperty("mediaId").GetString();

                    Check("image: an upload comes back as an id",
                        uploaded.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(mediaId),
                        $"status={uploaded.StatusCode} id={mediaId}");

                    await wireHttp.PostAsJsonAsync("/api/input", new
                    {
                        source = "text",
                        payload = "看看这张图",
                        media = new[] { mediaId },
                    });

                    var settledImage = false;
                    for (var attempt = 0; attempt < 400 && !settledImage; attempt++)
                    {
                        var polled = await wireHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        settledImage = !polled.GetProperty("busy").GetBoolean()
                            && polled.GetProperty("items").EnumerateArray().Any(item =>
                                item.GetProperty("kind").GetString() == "Assistant"
                                && item.GetProperty("body").GetString() == "通过真实 HTTP 返回。");
                        await Task.Delay(25);
                    }

                    var request = provider.LastRequestBody ?? string.Empty;
                    Check("image: the model receives the attachment inline",
                        settledImage
                        && request.Contains("\"image_url\"", StringComparison.Ordinal)
                        && request.Contains("data:image/png;base64,", StringComparison.Ordinal),
                        $"settled={settledImage} imageUrl={request.Contains("\"image_url\"", StringComparison.Ordinal)}");

                    // The session and queue logs stay text: they hold the id, never the bytes.
                    var sessionLog = await ReadSharedAsync(Path.Combine(wireWorkspace, "session", "events.jsonl"));
                    var queueLog = await ReadSharedAsync(Path.Combine(wireWorkspace, "queue", "items.jsonl"));

                    Check("image: the logs keep the id, not the bytes",
                        queueLog.Contains(mediaId!, StringComparison.Ordinal)
                        && !queueLog.Contains("iVBORw0KGgo", StringComparison.Ordinal)
                        && !sessionLog.Contains("iVBORw0KGgo", StringComparison.Ordinal),
                        $"queueHasId={queueLog.Contains(mediaId!, StringComparison.Ordinal)}");
                }

                // The desktop capability end to end, with a scripted model: model → governance → tool → desktop.
                // Nothing is injected here — the action is denied by the approval queue, which is the path that
                // protects the user's pointer, and the smoke never takes it upon itself to click.
                // The model is dynamic: it reads what it just observed and asks to press a key at a real window
                // that is *not* in front, so the guards refuse it before anything is injected. The point is what
                // happens *before* that: a slow approval outlives the lease, and the action must still be about
                // the same window.
                var leaseWorkspace = Path.Combine(root, "desktop-lease");
                Directory.CreateDirectory(Path.Combine(leaseWorkspace, "settings"));
                await File.WriteAllTextAsync(
                    Path.Combine(leaseWorkspace, "settings", "runtime.json"),
                    """{"model":{"supportsImages":true},"desktop":{"enabled":true,"leaseTtlMs":1500,"allowedWindowTitles":[]}}""");

                // The target is a background window owned by this run.
                using var leaseWindow = OwnedWindow.Create(LeaseProbeTitle, showWithoutActivating: true);

                var leasePort = FreePort();
                var leaseToken = TokenAuth.Generate();
                var leaseLlm = new ObserveThenActLlm(LeaseProbeTitle);

                var (leaseApp, leaseContext, leaseConsumer, leaseVoice, _, _) =
                    await ApiHost.BuildAsync(leaseWorkspace, leasePort, leaseToken, clock, new DesktopAgentShim(leaseLlm));
                var leaseRun = leaseApp.RunAsync();
                using var leaseHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{leasePort}") };
                leaseHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", leaseToken);

                try
                {
                    await WaitForHostAsync(leaseHttp);
                    await leaseHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "按一下 C" });

                    // The action parks in the approval queue.
                    string? parked = null;
                    for (var attempt = 0; attempt < 600 && parked is null; attempt++)
                    {
                        var parkedQueue = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/approvals");
                        var pending = parkedQueue.GetProperty("pending").EnumerateArray().ToList();
                        if (pending.Count > 0)
                        {
                            parked = pending[0].GetProperty("approvalId").GetString();
                        }

                        await Task.Delay(25);
                    }

                    Check("desktop: a key press waits for approval", parked is not null, $"id={parked}");

                    var obsBefore = (await leaseHttp.GetFromJsonAsync<JsonElement>("/api/desktop"))
                        .GetProperty("lastObsId").GetString();

                    // Wait well past the lease TTL (the policy floors it at 2s), then approve: the lease is long
                    // dead, the window is not. Acting without looking again would be refused as Expired here.
                    await Task.Delay(6000);
                    if (parked is not null)
                    {
                        await leaseHttp.PostAsJsonAsync($"/api/approvals/{parked}", new { approved = true });
                    }

                    string? outcome = null;
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        var polled = await JobSessionAsync(leaseHttp);
                        var results = polled.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                            .ToList();

                        // The keystroke's refusal specifically: by the time the approval lands the turn may
                        // already have moved on, and "the last result" then means something else.
                        var refusal = results
                            .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                            .LastOrDefault(body => body.Contains("前台", StringComparison.Ordinal));

                        if (refusal is not null)
                        {
                            outcome = refusal;
                            break;
                        }

                        await Task.Delay(25);
                    }

                    // The keystroke was refused (that window is not in front), so nothing was typed — and the tool
                    // looked again before deciding, which is what makes a lease that died during the approval
                    // harmless. Without the re-look the lease is dead and the action is refused as Expired.
                    var obsAfter = (await leaseHttp.GetFromJsonAsync<JsonElement>("/api/desktop"))
                        .GetProperty("lastObsId").GetString();

                    Check("desktop: an approved action looks again instead of trusting a dead lease",
                        outcome is not null
                        && outcome.Contains("前台", StringComparison.Ordinal)
                        && obsAfter != obsBefore,
                        $"reObserved={obsAfter != obsBefore} result={(outcome ?? "(none)")[..Math.Min(70, (outcome ?? "(none)").Length)]}");

                    // The blind-app fallback: a screenshot reaches the model as an image, and the log keeps only the
                    // media id. The model reads the pixels itself instead of OCR. Waited for, because the turn is
                    // still running when the approval lands.
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        var hasImage = leaseLlm.Requests
                            .SelectMany(request => request.Messages)
                            .Any(message => message.Images is { Count: > 0 });

                        if (hasImage)
                        {
                            break;
                        }

                        await Task.Delay(25);
                    }

                    // One snapshot of the requests for the assertions below.
                    var leaseRequests = leaseLlm.Requests;
                    var shown = leaseRequests
                        .SelectMany(request => request.Messages)
                        .Where(message => message.Images is { Count: > 0 })
                        .ToList();

                    var screenshotResult = await JobSessionAsync(leaseHttp);
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        if (screenshotResult.GetProperty("items").EnumerateArray()
                            .Any(item => (item.GetProperty("body").GetString() ?? string.Empty).Contains("媒体 ", StringComparison.Ordinal)))
                        {
                            break;
                        }

                        await Task.Delay(25);
                        screenshotResult = await JobSessionAsync(leaseHttp);
                    }
                    var shot = screenshotResult.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                        .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                        .LastOrDefault(body => body.Contains("媒体 ", StringComparison.Ordinal));

                    // The turn keeps going after the screenshot: the bare-point move, then the point no window
                    // covers. Waited for, because those results are the point of the check — and approved as they
                    // arrive, because every desktop_act parks (the gear is on "all" in this workspace).
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        var bodies = screenshotResult.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                            .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                            .ToList();

                        // The act answer now starts with the {"code","delivery","retry"} contract line, so the
                        // kind is matched anywhere in the body rather than at its start.
                        if (bodies.Any(body => body.Contains("MovePointer", StringComparison.Ordinal))
                            && bodies.Any(body => body.Contains("不落在唯一一个已观察窗口内", StringComparison.Ordinal)))
                        {
                            break;
                        }

                        foreach (var pending in (await leaseHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                            .GetProperty("pending").EnumerateArray().ToList())
                        {
                            await leaseHttp.PostAsJsonAsync(
                                $"/api/approvals/{pending.GetProperty("approvalId").GetString()}",
                                new { approved = true });
                        }

                        await Task.Delay(25);
                        screenshotResult = await JobSessionAsync(leaseHttp);
                    }

                    var allResults = screenshotResult.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                        .Select(item => (item.GetProperty("body").GetString() ?? string.Empty).Replace("\n", " "))
                        .ToList();

                    // The failure this check has to be diagnosable for: a lease that expired while the model was
                    // thinking. The timestamps are what say whether the action was late or the lease was short.
                    var timeline = string.Join(" | ", screenshotResult.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() is "ToolCall" or "ToolResult")
                        .Select(item => item.TryGetProperty("at", out var at)
                            ? $"{at.GetDateTime():HH:mm:ss.fff} {(item.GetProperty("body").GetString() ?? string.Empty)[..Math.Min(28, (item.GetProperty("body").GetString() ?? string.Empty).Length)]}"
                            : item.GetRawText()[..Math.Min(40, item.GetRawText().Length)]));

                    Check("desktop: a screenshot reaches the model as an image",
                        shown.Count > 0 && shown[^1].Images!.Any(image => image.StartsWith("data:image/png;base64,", StringComparison.Ordinal)),
                        $"imageMessages={shown.Count} results=[{string.Join(" | ", allResults.Select(r => r[..Math.Min(60, r.Length)]))}]");

                    Check("desktop: the log records the screenshot by media id, not by pixels",
                        shot is not null && !screenshotResult.GetRawText().Contains("iVBORw0KGgo", StringComparison.Ordinal),
                        $"result={(shot ?? "(none)")[..Math.Min(60, (shot ?? "(none)").Length)]}");

                    // The call models actually make: coordinates, no window id. It must resolve rather than be
                    // refused for a missing entityId, which would burn the step budget with the app untouched.
                    var barePoint = allResults.LastOrDefault(r => r.Contains("MovePointer", StringComparison.Ordinal));

                    Check("desktop: a point with no window id resolves to the window that covers it",
                        barePoint is not null && barePoint.Contains("已发出", StringComparison.Ordinal),
                        $"result={barePoint ?? "(none)"} | every result=["
                            + string.Join(" | ", allResults.Select(body => body[..Math.Min(70, body.Length)]))
                            + $"] | timeline=[{timeline}]");

                    // And when no window covers the point, the refusal has to name the candidates: telling the model
                    // to "observe first" when it has just observed sends it into a loop.
                    var noWindow = allResults.LastOrDefault(r => r.Contains("不落在唯一一个已观察窗口内", StringComparison.Ordinal));

                    Check("desktop: a point no window covers is refused with the candidates named",
                        noWindow is not null
                            && noWindow.Contains("entityId", StringComparison.Ordinal)
                            && noWindow.Contains("'", StringComparison.Ordinal),
                        $"result={(noWindow ?? "(none)")[..Math.Min(90, (noWindow ?? "(none)").Length)]}");

                    // The telemetry aggregates: the turn above ran through observe → act → screenshot, so the
                    // endpoint must hold at least one act and one picture — without those, a tuning decision has
                    // no denominator. Verdicts are NOT required non-zero: the scripted turn refuses most acts
                    // (guards) and the aim-only kinds promise no picture, so every verdict field can legitimately
                    // be 0 on a scripted turn.
                    var telemetry = await leaseHttp.GetFromJsonAsync<JsonElement>("/api/desktop/telemetry");
                    var hasAct = telemetry.GetProperty("acts").GetInt32() > 0;
                    var hasPicture = telemetry.GetProperty("pictures").GetProperty("count").GetInt32() > 0;

                    Check("desktop: telemetry aggregates the turn the model just ran",
                        hasAct && hasPicture,
                        $"acts={(telemetry.GetProperty("acts").GetInt32())} "
                            + $"pictures={(telemetry.GetProperty("pictures").GetProperty("count").GetInt32())} "
                            + $"verdicts={telemetry.GetProperty("verdicts").GetRawText()} "
                            + $"walkTruncated={telemetry.GetProperty("walkTruncated").GetInt32()}");
                }
                finally
                {
                    await leaseApp.StopAsync();
                    await leaseRun;
                    await leaseVoice.DisposeAsync();
                    await leaseConsumer.DisposeAsync();
                    await leaseContext.DisposeAsync();
                }

                var grantWorkspace = Path.Combine(root, "desktop-lease-grant");
                Directory.CreateDirectory(Path.Combine(grantWorkspace, "settings"));
                await File.WriteAllTextAsync(
                    Path.Combine(grantWorkspace, "settings", "runtime.json"),
                    """{"model":{"supportsImages":false},"desktop":{"enabled":true,"leaseTtlMs":15000,"allowedWindowTitles":[]}}""");

                using var grantWindow = OwnedWindow.Create(GrantProbeTitle, showWithoutActivating: true);

                var grantConsumerLlm = new LeaseThenActLlm(GrantProbeTitle);
                var grantPort = FreePort();
                var grantToken = TokenAuth.Generate();
                var grantClock = new OffsetClock(new DateTimeOffset(2026, 9, 27, 9, 30, 0, TimeSpan.Zero));
                var (grantApp, grantContext, grantConsumer, grantVoice, _, _) =
                    await ApiHost.BuildAsync(grantWorkspace, grantPort, grantToken, grantClock, new DesktopAgentShim(grantConsumerLlm));
                var grantRun = grantApp.RunAsync();
                using var grantHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{grantPort}") };
                grantHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grantToken);

                try
                {
                    await WaitForHostAsync(grantHttp);
                    await grantHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "连续操作一个窗口" });

                    // The first act parks; approved here, which is where the continuing grant is minted —
                    // not through any desktop endpoint, because a "yes" is the human's whole intent.
                    string? grantApprovalId = null;
                    for (var attempt = 0; attempt < 600 && grantApprovalId is null; attempt++)
                    {
                        var pending = (await grantHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                            .GetProperty("pending").EnumerateArray().ToList();
                        if (pending.Count > 0)
                        {
                            grantApprovalId = pending[0].GetProperty("approvalId").GetString();
                        }

                        await Task.Delay(25);
                    }
                    var granted = grantApprovalId is not null
                        && (await grantHttp.PostAsJsonAsync($"/api/approvals/{grantApprovalId}", new { approved = true })).IsSuccessStatusCode;

                    // The granted state must be server-visible, so a run and a restart see the same fact.
                    TaskLeaseState? shownLease = null;
                    for (var attempt = 0; attempt < 600 && shownLease is null; attempt++)
                    {
                        shownLease = grantContext.TaskLeases.Snapshot().FirstOrDefault();
                        await Task.Delay(25);
                    }

                    Check("desktop: an approved action opens a continuing grant on its window",
                        granted && shownLease is not null
                        && shownLease.EntityId == PickTargetOf(grantConsumerLlm.Requests, GrantProbeTitle)
                        && shownLease.RemainingSteps == Api.Settings.TaskLeaseStore.DefaultSteps,
                        shownLease is null ? "no lease was minted" : $"entity={shownLease.EntityId} steps={shownLease.RemainingSteps}");

                    // The second act on the same window is what the grant is for: it must not park, and it
                    // must reach the tool (where the guard refuses it — the window is not in front), which
                    // costs one of the ten.
                    var secondResult = default(string?);
                    var secondActParked = false;
                    for (var attempt = 0; attempt < 400 && secondResult is null; attempt++)
                    {
                        var pending = (await grantHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                            .GetProperty("pending").EnumerateArray().ToList();
                        if (pending.Count > 0 && pending[0].GetProperty("toolName").GetString() == "desktop_act")
                        {
                            // A parked second act is the failure this check exists to catch — deny it so the
                            // turn finishes, and report it precisely rather than as a timeout.
                            secondActParked = true;
                            secondResult = "(still parked: governance did not see the grant)";
                            await grantHttp.PostAsJsonAsync(
                                $"/api/approvals/{pending[0].GetProperty("approvalId").GetString()}",
                                new { approved = false });
                            break;
                        }

                        var polled = await JobSessionAsync(grantHttp);
                        var results = polled.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                            .Select(item => item.GetProperty("body").GetString() ?? string.Empty)
                            .Where(body => body.Contains("不在前台", StringComparison.Ordinal))
                            .ToList();

                        // The first act's result is the guard refusal it earned the grant; find the second one.
                        if (results.Count >= 2)
                        {
                            secondResult = results[^1];
                            break;
                        }

                        await Task.Delay(25);
                    }

                    var spentLease = grantContext.TaskLeases.Snapshot().FirstOrDefault();
                    Check("desktop: a granted window does not re-ask for the same kind",
                        secondResult is not null && !secondActParked
                        && spentLease is not null
                        && spentLease.RemainingSteps == Api.Settings.TaskLeaseStore.DefaultSteps - 1,
                        $"result={secondResult ?? "(timed out)"} "
                        + $"remaining={spentLease?.RemainingSteps.ToString() ?? "none"}");

                    // What a grant must never cover: leaving the GUI parks even under a live grant.
                    string? execApproval = null;
                    for (var attempt = 0; attempt < 600 && execApproval is null; attempt++)
                    {
                        var pending = (await grantHttp.GetFromJsonAsync<JsonElement>("/api/approvals"))
                            .GetProperty("pending").EnumerateArray().ToList();
                        if (pending.Count > 0 && pending[0].GetProperty("toolName").GetString() == "desktop_exec")
                        {
                            execApproval = pending[0].GetProperty("approvalId").GetString();
                        }

                        await Task.Delay(25);
                    }

                    if (execApproval is not null)
                    {
                        await grantHttp.PostAsJsonAsync($"/api/approvals/{execApproval}", new { approved = false });
                    }

                    // A denied exec is still “the tool could not run”: it settles without losing the grant.
                    TaskLeaseState? afterDeny = null;
                    for (var attempt = 0; attempt < 400; attempt++)
                    {
                        var polled = await JobSessionAsync(grantHttp);
                        if (polled.GetProperty("items").EnumerateArray()
                            .Any(item => (item.GetProperty("body").GetString() ?? string.Empty)
                                .Contains("denied:", StringComparison.Ordinal)))
                        {
                            afterDeny = grantContext.TaskLeases.Snapshot().FirstOrDefault();
                            break;
                        }

                        await Task.Delay(25);
                    }

                    Check("desktop: a grant never covers leaving the GUI even while one is live",
                        execApproval is not null
                        && afterDeny is not null
                        && afterDeny.RemainingSteps == Api.Settings.TaskLeaseStore.DefaultSteps - 1,
                        execApproval is null ? "desktop_exec never parked" : $"denied, grant steps={afterDeny?.RemainingSteps.ToString() ?? "(none)"}");
                }
                finally
                {
                    await grantApp.StopAsync();
                    await grantRun;
                    await grantVoice.DisposeAsync();
                    await grantConsumer.DisposeAsync();
                    await grantContext.DisposeAsync();
                }

                var desktopWorkspace = Path.Combine(root, "desktop-tools");
                Directory.CreateDirectory(Path.Combine(desktopWorkspace, "settings"));
                await File.WriteAllTextAsync(
                    Path.Combine(desktopWorkspace, "settings", "runtime.json"),
                    """{"desktop":{"enabled":true,"leaseTtlMs":15000,"allowedWindowTitles":[]}}""");

                var desktopPort = FreePort();
                var desktopToken = TokenAuth.Generate();
                var desktopScripted = new ScriptedLlmClient(new[]
                {
                // Turn 1: look. Read-only, so it runs without approval.
                new LlmResponse(null, new[] { new LlmToolCall("c1", "desktop_observe", "{}") }, "tool_calls"),
                new LlmResponse("看过了。", Array.Empty<LlmToolCall>(), "stop"),
                // Turn 2: act. Irreversible, so it parks in the approval queue.
                new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c2", "desktop_act", """{"entityId":"deadbeef","kind":"LeftClick","x":1,"y":1}""") },
                    "tool_calls"),
                new LlmResponse("好吧。", Array.Empty<LlmToolCall>(), "stop"),
            });

                var (desktopApp, desktopContext, desktopConsumer, desktopVoice, _, _) =
                    await ApiHost.BuildAsync(desktopWorkspace, desktopPort, desktopToken, clock, new DesktopAgentShim(desktopScripted));
                var desktopRun = desktopApp.RunAsync();
                using var desktopHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{desktopPort}") };
                desktopHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", desktopToken);

                try
                {
                    await WaitForHostAsync(desktopHttp);

                    async Task<List<string>> ExposedToolsAsync()
                    {
                        // The desktop tools belong to the desktop sub-agent now, so that is where the table is read.
                        var consumer = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/consumer");
                        return consumer.GetProperty("agentTools").EnumerateArray()
                            .First(agent => agent.GetProperty("scenario").GetString() == "desktop")
                            .GetProperty("tools").EnumerateArray()
                            .Select(tool => $"{tool.GetProperty("name").GetString()}:{tool.GetProperty("decision").GetString()}")
                            .ToList();
                    }

                    var gearAll = await ExposedToolsAsync();
                    Check("desktop: the default gear asks for every action but never for looking",
                        gearAll.Contains("desktop_observe:allow")
                        && gearAll.Contains("desktop_act:ask")
                        && gearAll.Contains("desktop_exec:ask"),
                        string.Join(",", gearAll));

                    await desktopHttp.PostAsJsonAsync("/api/desktop/approval", new { approval = "dangerous" });
                    var gearDangerous = await ExposedToolsAsync();
                    Check("desktop: the middle gear frees the GUI and still gates commands",
                        gearDangerous.Contains("desktop_act:allow")
                        && gearDangerous.Contains("desktop_exec:ask")
                        && gearDangerous.Contains("desktop_launch:ask"),
                        string.Join(",", gearDangerous));

                    await desktopHttp.PostAsJsonAsync("/api/desktop/approval", new { approval = "none" });
                    var gearNone = await ExposedToolsAsync();
                    Check("desktop: the open gear asks for nothing",
                        gearNone.All(tool => tool.EndsWith(":allow", StringComparison.Ordinal)),
                        string.Join(",", gearNone));

                    await desktopHttp.PostAsJsonAsync("/api/desktop/approval", new { approval = "all" });

                    var state = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/desktop");
                    Check("desktop: the capability reports itself enabled",
                        state.GetProperty("enabled").GetBoolean(),
                        $"enabled={state.GetProperty("enabled").GetBoolean()}");

                    await desktopHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "看看桌面" });

                    var observed = false;
                    string? observeResult = null;
                    for (var attempt = 0; attempt < 400 && !observed; attempt++)
                    {
                        var polled = await JobSessionAsync(desktopHttp);
                        var results = polled.GetProperty("items").EnumerateArray()
                            .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                            .ToList();

                        if (results.Count > 0)
                        {
                            observeResult = results[^1].GetProperty("body").GetString();
                            observed = true;
                        }

                        await Task.Delay(25);
                    }

                    // The model's tool call reached the real desktop: the result names the windows it enumerated.
                    Check("desktop: the model can observe through its tool",
                        observed
                        && observeResult is not null
                        && observeResult.Contains("\"obsId\"", StringComparison.Ordinal)
                        // The controls, not just the windows: addressing a named button is the whole point.
                        && observeResult.Contains("\"elementId\"", StringComparison.Ordinal),
                        $"result={(observeResult ?? "(none)")[..Math.Min(80, (observeResult ?? "(none)").Length)]}");

                    // Acting is irreversible, so governance parks it. The smoke answers — and denies.
                    // One job at a time per sub-agent: the second request waits for the first to report.
                    await WaitJobsIdleAsync(desktopHttp);
                    await desktopHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "点一下" });

                    string? approvalId = null;
                    for (var attempt = 0; attempt < 400 && approvalId is null; attempt++)
                    {
                        var approvalQueue = await desktopHttp.GetFromJsonAsync<JsonElement>("/api/approvals");
                        var pending = approvalQueue.GetProperty("pending").EnumerateArray().ToList();
                        if (pending.Count > 0)
                        {
                            approvalId = pending[0].GetProperty("approvalId").GetString();
                            Check("desktop: an action waits for approval instead of running",
                                pending[0].GetProperty("toolName").GetString() == "desktop_act",
                                pending[0].GetProperty("toolName").GetString() ?? "(none)");
                        }

                        await Task.Delay(25);
                    }

                    Check("desktop: the approval queue surfaces the parked action", approvalId is not null, $"id={approvalId}");

                    if (approvalId is not null)
                    {
                        var answered = await desktopHttp.PostAsJsonAsync(
                            $"/api/approvals/{approvalId}",
                            new { approved = false });

                        Check("desktop: answering the queue settles it", answered.IsSuccessStatusCode, $"status={answered.StatusCode}");

                        var denied = false;
                        string? denyResult = null;
                        for (var attempt = 0; attempt < 400 && !denied; attempt++)
                        {
                            var polled = await JobSessionAsync(desktopHttp);
                            var results = polled.GetProperty("items").EnumerateArray()
                                .Where(item => item.GetProperty("kind").GetString() == "ToolResult")
                                .ToList();

                            if (results.Count > 1)
                            {
                                denyResult = results[^1].GetProperty("body").GetString();
                                denied = denyResult is not null && denyResult.Contains("approval denied", StringComparison.Ordinal);
                            }

                            await Task.Delay(25);
                        }

                        // Nothing was injected: the refusal is what the model gets back, and the turn continues.
                        Check("desktop: a denied action is refused, not performed",
                            denied,
                            $"result={(denyResult ?? "(none)")[..Math.Min(90, (denyResult ?? "(none)").Length)]}");
                    }

                    // The tool calls are in the sub-agent's own session, not the main one.
                    var log = (await JobSessionAsync(desktopHttp)).GetRawText();
                    Check("desktop: the refusal is in the log with the tool's name",
                        log.Contains("desktop_act", StringComparison.Ordinal)
                        && log.Contains("approval denied", StringComparison.Ordinal),
                        $"hasCall={log.Contains("\"desktop_act\"", StringComparison.Ordinal)}");
                }
                finally
                {
                    await desktopApp.StopAsync();
                    await desktopRun;
                    await desktopVoice.DisposeAsync();
                    await desktopConsumer.DisposeAsync();
                    await desktopContext.DisposeAsync();
                }


            }
            finally
            {
                await wireApp.StopAsync();
                await wireRun;
                await wireVoice.DisposeAsync();
                await wireConsumer.DisposeAsync();
                await wireContext.DisposeAsync();
            }

            await SettingsApiChecksAsync(root, clock);
            await HostedVoiceChecksAsync(root, clock);
        }
    }

    private static async Task SettingsApiChecksAsync(string root, TimeProvider clock)
    {
        {
            // Settings are the configuration surface, so pointing them at an endpoint must re-point the
            // running consumer — not just change what the UI displays.
            var settingsWorkspace = Path.Combine(root, "consumer-settings");
            var settingsPort = FreePort();
            var settingsToken = TokenAuth.Generate();
            var settingsModelPort = FreePort();

            using var settingsProvider = new FakeChatProvider(settingsModelPort, "由设置驱动的回复。");
            var (settingsApp, settingsContext, settingsConsumer, settingsVoice, _, _) =
                await ApiHost.BuildAsync(settingsWorkspace, settingsPort, settingsToken, clock);
            var settingsRun = settingsApp.RunAsync();
            using var settingsHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settingsPort}") };
            settingsHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settingsToken);

            try
            {
                await WaitForHostAsync(settingsHttp);

                var put = await settingsHttp.PutAsJsonAsync("/api/settings", new
                {
                    revision = (await settingsHttp.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("revision").GetInt64(),
                    model = new
                    {
                        baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                        model = "settings-model",
                        apiKey = "smoke-key",
                        reasoningEffort = "XHIGH ",
                    },
                });
                var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
                var reconfigured = putBody.GetProperty("consumer");

                var delivered = await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "设置驱动" });
                var deliveredId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var settled = await WaitForQueueItemToSettleAsync(settingsHttp, deliveredId);

                var session = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var assistant = session.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("kind").GetString() == "Assistant")
                    .Select(item => item.GetProperty("body").GetString())
                    .ToList();

                Check("settings: pointing the model settings at an endpoint re-points the consumer",
                    reconfigured.GetProperty("running").GetBoolean()
                    && reconfigured.GetProperty("model").GetString() == "settings-model"
                    && reconfigured.GetProperty("reason").GetString()!.Contains("设置", StringComparison.Ordinal)
                    && settled
                    && assistant.Count == 1
                    && assistant[0] == "由设置驱动的回复。"
                    && settingsProvider.SawAuthorization == "Bearer smoke-key",
                    $"running={reconfigured.GetProperty("running").GetBoolean()} settled={settled} reply={assistant.FirstOrDefault()}");

                // Discovery: the UI offers what the runtime can see rather than asking for names.
                var deviceList = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/voice/devices");
                var probe = await settingsHttp.PostAsJsonAsync("/api/models", new
                {
                    baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                });
                var probeBody = await probe.Content.ReadFromJsonAsync<JsonElement>();
                var probed = probeBody.GetProperty("models").EnumerateArray().Select(m => m.GetString()).ToList();

                Check("settings: the model listing is pulled through the API",
                    probed.SequenceEqual(new[] { "smoke-model-a", "smoke-model-b" })
                    && !probeBody.TryGetProperty("error", out _),
                    $"models={string.Join(",", probed)}");

                var dead = await settingsHttp.PostAsJsonAsync("/api/models", new
                {
                    baseUrl = $"http://127.0.0.1:{FreePort()}/v1",
                });
                var deadBody = await dead.Content.ReadFromJsonAsync<JsonElement>();

                Check("settings: an unreachable model endpoint returns a reason, not a silent empty list",
                    deadBody.GetProperty("models").GetArrayLength() == 0
                    && !string.IsNullOrWhiteSpace(deadBody.GetProperty("error").GetString()),
                    deadBody.GetProperty("error").GetString() ?? "(no reason)");

                // A window containing hook injections and tool results must not be sent as system messages in
                // the middle of the conversation: providers reject those outright. The first turn has an empty
                // window, so only a second turn can catch it.
                var second = await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "第二轮" });
                var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var secondSettled = await WaitForQueueItemToSettleAsync(settingsHttp, secondId);

                var sent = settingsProvider.LastRequestBody ?? string.Empty;
                using var sentJson = JsonDocument.Parse(sent.Length == 0 ? "{}" : sent);
                var roles = sentJson.RootElement.TryGetProperty("messages", out var messages)
                    ? messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToList()
                    : new List<string>();

                Check("consumer: a second turn sends no system message after the first",
                    secondSettled && roles.Count > 0 && roles[0] == "system" && !roles.Skip(1).Contains("system"),
                    $"settled={secondSettled} bodyLen={sent.Length} path={settingsProvider.RequestPath} roles={string.Join(",", roles)}");

                // The gear travels settings → the client the consumer builds → the wire. A gear that is saved
                // and never sent is the kind of setting that looks configured and does nothing.
                var gear = sentJson.RootElement.TryGetProperty("reasoning_effort", out var sentGear)
                    ? sentGear.GetString()
                    : "(absent)";

                Check("settings: the thinking gear reaches the model request",
                    gear == "xhigh",
                    $"reasoning_effort={gear} bodyLen={sent.Length}");

                // The same gear, chosen for one message instead of for the runtime. This is the hop the
                // composer drives, and it is the one a settings-only check cannot see: the value has to
                // leave /api/input, ride the queue item, and win over the configured default on the wire.
                // The whole model section, not just the gear: a partial PUT replaces the record, so omitting
                // the endpoint here would blank it and fail validation instead of changing the menu.
                var perTurnPut = await settingsHttp.PutAsJsonAsync("/api/settings", new
                {
                    revision = (await settingsHttp.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("revision").GetInt64(),
                    model = new
                    {
                        baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                        model = "settings-model",
                        // Carried over: the record is replaced wholesale, and the next check asserts the key
                        // is still stored but never echoed back.
                        apiKey = "smoke-key",
                        reasoningEffort = "low",
                        reasoningGears = new[] { "low", "scenic" },
                    },
                });
                await perTurnPut.Content.ReadAsStringAsync();

                var perTurn = await settingsHttp.PostAsJsonAsync("/api/input", new
                {
                    source = "text",
                    payload = "这一条换个档",
                    reasoningEffort = "scenic",
                });
                var perTurnId = (await perTurn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

                var perTurnSettled = await WaitForQueueItemToSettleAsync(settingsHttp, perTurnId);

                var perTurnBody = settingsProvider.LastRequestBody ?? string.Empty;
                using var perTurnJson = JsonDocument.Parse(perTurnBody.Length == 0 ? "{}" : perTurnBody);
                var perTurnGear = perTurnJson.RootElement.TryGetProperty("reasoning_effort", out var onWire)
                    ? onWire.GetString()
                    : "(absent)";

                Check("input: a gear chosen for one message wins over the configured default",
                    perTurnSettled && perTurnGear == "scenic",
                    $"settled={perTurnSettled} reasoning_effort={perTurnGear}");

                // A word the menu does not carry must not reach the model: the composer only ever offers
                // listed gears, so anything else is a stale client and the turn would silently differ.
                var offMenu = await settingsHttp.PostAsJsonAsync("/api/input", new
                {
                    source = "text",
                    payload = "这档不存在",
                    reasoningEffort = "turbo",
                });

                Check("input: a gear that is not on the menu is rejected and lists the ones that are",
                    offMenu.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && (await offMenu.Content.ReadAsStringAsync()).Contains("scenic", StringComparison.Ordinal),
                    $"status={(int)offMenu.StatusCode}");

                // The key went out on the wire but must never come back through the settings endpoint.
                var raw = await settingsHttp.GetStringAsync("/api/settings");
                Check("settings: the API key is sent to the model but never echoed back",
                    !raw.Contains("smoke-key", StringComparison.Ordinal)
                    && JsonDocument.Parse(raw).RootElement.GetProperty("model").GetProperty("apiKeySet").GetBoolean(),
                    "apiKeySet=true, value absent");

                // Removing history: the view and the log both lose it, and the conversation continues.
                var doomed = await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "要删掉的" });
                var doomedId = (await doomed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();
                await WaitForQueueItemToSettleAsync(settingsHttp, doomedId);

                var beforeRemoval = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var target = beforeRemoval.GetProperty("items").EnumerateArray()
                    .First(i => i.GetProperty("body").GetString() == "要删掉的")
                    .GetProperty("seq").GetInt64();

                // The log is JSON with CJK escaped, so it is parsed rather than searched: a raw Contains would
                // report "gone" for text that is still there.
                static bool LogHolds(string workspace, string body)
                {
                    var path = Path.Combine(workspace, "session", "events.jsonl");
                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (SessionJson.Deserialize(line)?.Body == body)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var delete = await settingsHttp.PostAsync($"/api/session/{target}/delete", content: null);
                var afterDelete = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var stillThere = afterDelete.GetProperty("items").EnumerateArray()
                    .Any(i => i.GetProperty("seq").GetInt64() == target);

                Check("session: deleting the latest conversation takes its text out of the log",
                    delete.IsSuccessStatusCode
                    && !stillThere
                    && !LogHolds(settingsWorkspace, "要删掉的")
                    && afterDelete.GetProperty("lastSeq").GetInt64() > beforeRemoval.GetProperty("lastSeq").GetInt64(),
                    $"deleted={delete.IsSuccessStatusCode} visible={stillThere} "
                    + $"inLog={LogHolds(settingsWorkspace, "要删掉的")} "
                    + $"lastSeq {beforeRemoval.GetProperty("lastSeq").GetInt64()}->{afterDelete.GetProperty("lastSeq").GetInt64()}");

                // Withdrawing hides everything from the selected conversation onward, as of now — and deletes nothing
                // until a new message is sent, which is what lets it be taken back.
                var keptItem = afterDelete.GetProperty("items").EnumerateArray().First();
                var kept = keptItem.GetProperty("seq").GetInt64();
                var keptBody = keptItem.GetProperty("body").GetString() ?? string.Empty;
                var withdraw = await settingsHttp.PostAsync($"/api/session/{kept}/withdraw", content: null);
                var afterWithdraw = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");

                Check("session: withdrawing hides the conversation but leaves its text in the log",
                    withdraw.IsSuccessStatusCode
                    && afterWithdraw.GetProperty("items").GetArrayLength() == 0
                    && afterWithdraw.GetProperty("conversation").GetProperty("withdrawn").ValueKind == JsonValueKind.Object
                    && LogHolds(settingsWorkspace, keptBody),
                    $"withdrawn={withdraw.IsSuccessStatusCode} "
                    + $"remaining={afterWithdraw.GetProperty("items").GetArrayLength()} "
                    + $"inLog={LogHolds(settingsWorkspace, keptBody)}");

                // Taken back: the view is what it was.
                var cancelWithdrawal = await settingsHttp.PostAsync("/api/session/withdrawal/cancel", content: null);
                var afterCancel = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");

                Check("session: a withdrawal can be cancelled before anything new is sent",
                    cancelWithdrawal.IsSuccessStatusCode
                    && afterCancel.GetProperty("items").GetArrayLength() == afterDelete.GetProperty("items").GetArrayLength()
                    && afterCancel.GetProperty("conversation").GetProperty("withdrawn").ValueKind == JsonValueKind.Null,
                    $"cancelled={cancelWithdrawal.IsSuccessStatusCode} "
                    + $"items {afterDelete.GetProperty("items").GetArrayLength()}->{afterCancel.GetProperty("items").GetArrayLength()}");

                // Withdrawn again, and now a new message is sent: that makes it final.
                await settingsHttp.PostAsync($"/api/session/{kept}/withdraw", content: null);
                await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "撤回之后" });
                var revived = false;
                for (var attempt = 0; attempt < 600 && !revived; attempt++)
                {
                    var polled = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    revived = polled.GetProperty("items").EnumerateArray().Any(i =>
                        i.GetProperty("body").GetString() == "撤回之后");
                    await Task.Delay(25);
                }

                Check("session: sending a new message makes the withdrawal final",
                    !LogHolds(settingsWorkspace, keptBody)
                    && (await settingsHttp.PostAsync("/api/session/withdrawal/cancel", content: null)).StatusCode == System.Net.HttpStatusCode.NotFound,
                    $"inLog={LogHolds(settingsWorkspace, keptBody)}");

                Check("session: events after a retraction stay visible", revived, $"revived={revived}");

                // Shrinking the context folds history once, using the model that is already configured. Enough
                // history is needed first: folding one event is not compaction. The wait is on the session's own
                // sequence — a turn that is mid-flight is not in the history yet.
                // Counted in content events, not in seq: a turn also appends turnStart/stepStart/stepEnd/turnEnd,
                // so waiting for "six more seqs" can be satisfied by the control events of one turn while the
                // other two are still running — and then the shrink happens before there is history to fold.
                static int ContentCount(JsonElement session) => session.GetProperty("items").EnumerateArray()
                    .Count(item => item.GetProperty("kind").GetString() is "User" or "Assistant");

                var fillContent = ContentCount(await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session"));

                for (var i = 0; i < 3; i++)
                {
                    await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = $"填充 {i}" });
                }

                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    if (ContentCount(polled) >= fillContent + 6)
                    {
                        break;
                    }

                    await Task.Delay(25);
                }

                // The context meter: the occupancy reported to the UI is the one that decides folding. Measured
                // against the roomy default window, where another turn is a visible increase. The wait is on the
                // session itself — queue depth can read zero while a turn is mid-flight, and then the reply this
                // check is about has not been written yet.
                var usageView = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var usageBefore = usageView.GetProperty("context");
                var seqBefore = usageView.GetProperty("lastSeq").GetInt64();

                await settingsHttp.PostAsJsonAsync("/api/input", new { source = "text", payload = "再填充一些内容让占用增长" });

                var usageAfter = usageBefore;
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    var polled = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                    if (polled.GetProperty("lastSeq").GetInt64() >= seqBefore + 2)
                    {
                        usageAfter = polled.GetProperty("context");
                        break;
                    }

                    await Task.Delay(25);
                }

                Check("context: the reported occupancy follows the history",
                    usageAfter.GetProperty("usedTokens").GetInt32() > usageBefore.GetProperty("usedTokens").GetInt32()
                    && usageAfter.GetProperty("usedTokens").GetInt32() <= usageAfter.GetProperty("budgetTokens").GetInt32(),
                    $"{usageBefore.GetProperty("usedTokens").GetInt32()} -> {usageAfter.GetProperty("usedTokens").GetInt32()} "
                    + $"budget={usageAfter.GetProperty("budgetTokens").GetInt32()}");

                var beforeShrink = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var shrink = await settingsHttp.PutAsJsonAsync("/api/settings", new
                {
                    revision = (await settingsHttp.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("revision").GetInt64(),
                    model = new
                    {
                        baseUrl = $"http://127.0.0.1:{settingsModelPort}/v1",
                        model = "settings-model",
                        // ~5 tokens per short turn: a 16-token window with a 4-token reserve keeps two.
                        contextTokens = 16,
                        maxTokens = 4,
                    },
                });
                var shrinkBody = (await shrink.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("compaction");

                Check("settings: shrinking the context compacts the history once",
                    shrinkBody.GetProperty("compacted").GetBoolean()
                    && shrinkBody.GetProperty("foldedEvents").GetInt32() > 0,
                    $"items={beforeShrink.GetProperty("items").GetArrayLength()} "
                    + $"{shrinkBody.GetProperty("reason").GetString() ?? "(no reason)"}");

                var compacted = await settingsHttp.GetFromJsonAsync<JsonElement>("/api/session");
                var compactedUsage = compacted.GetProperty("context");
                var summaries = compacted.GetProperty("items").EnumerateArray()
                    .Count(i => i.GetProperty("kind").GetString() == "Summary");

                // After folding, the meter must show the folded history rather than the raw log. The window here
                // is deliberately tiny (16 tokens), so the always-sent system block alone can exceed the budget —
                // what matters is that folding lowered the prompt, not that it fits this artificial window.
                // FoldedItems counts entries the window had to drop, not entries a summary replaced: after a
                // successful fold everything that remains fits, so it is legitimately zero. The prompt cost is
                // what proves the meter moved to the folded history.
                Check("context: the meter reflects the folding",
                    compactedUsage.GetProperty("usedTokens").GetInt32() < usageAfter.GetProperty("usedTokens").GetInt32(),
                    $"{usageAfter.GetProperty("usedTokens").GetInt32()} -> {compactedUsage.GetProperty("usedTokens").GetInt32()} "
                    + $"budget={compactedUsage.GetProperty("budgetTokens").GetInt32()}");

                Check("context: the budget is the window minus the reserved output",
                    compactedUsage.GetProperty("budgetTokens").GetInt32()
                    == compactedUsage.GetProperty("contextTokens").GetInt32() - compactedUsage.GetProperty("maxTokens").GetInt32(),
                    $"{compactedUsage.GetProperty("contextTokens").GetInt32()} - {compactedUsage.GetProperty("maxTokens").GetInt32()}");

                Check("settings: the compaction leaves exactly one summary standing in for the history",
                    summaries == 1
                    && compacted.GetProperty("items").GetArrayLength() < beforeShrink.GetProperty("items").GetArrayLength(),
                    $"summaries={summaries} items={beforeShrink.GetProperty("items").GetArrayLength()}"
                    + $"->{compacted.GetProperty("items").GetArrayLength()}");
            }
            finally
            {
                await settingsApp.StopAsync();
                await settingsRun;
                await settingsVoice.DisposeAsync();
                await settingsConsumer.DisposeAsync();
                await settingsContext.DisposeAsync();
            }

        }
    }

    private static async Task HostedVoiceChecksAsync(string root, TimeProvider clock)
    {
        {
            // The whole product loop in one place: speak → queue → turn → session → speech.
            var loopWorkspace = Path.Combine(root, "voice-loop");
            var loopPort = FreePort();
            var loopToken = TokenAuth.Generate();
            var components = new SyntheticVoiceComponents("打开书房的灯");

            var loopLlm = new ScriptedLlmClient(new[]
            {
                new LlmResponse("书房的灯已经打开了。窗帘也拉上了。", Array.Empty<LlmToolCall>(), "stop"),
            });

            var (loopApp, loopContext, loopConsumer, loopVoice, _, _) =
                await ApiHost.BuildAsync(loopWorkspace, loopPort, loopToken, clock, loopLlm, components);
            var loopRun = loopApp.RunAsync();
            using var loopHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{loopPort}") };
            loopHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loopToken);

            try
            {
                await WaitForHostAsync(loopHttp);

                var enable = await loopHttp.PutAsJsonAsync("/api/settings", new
                {
                    revision = (await loopHttp.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("revision").GetInt64(),
                    input = new { enabled = true, utteranceGapMs = 200, silenceThreshold = 0.03 },
                    output = new { enabled = true },
                    asr = new { baseUrl = "http://127.0.0.1:45101", model = "smoke-asr" },
                    tts = new { baseUrl = "http://127.0.0.1:45102", lang = "zh" },
                });
                enable.EnsureSuccessStatusCode();
                var voiceState = (await enable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("voice");

                Check("voice: enabling the halves starts them on the configured devices",
                    voiceState.GetProperty("input").GetProperty("running").GetBoolean()
                    && voiceState.GetProperty("output").GetProperty("running").GetBoolean()
                    && voiceState.GetProperty("input").GetProperty("device").GetString() == "synthetic-mic",
                    $"in={voiceState.GetProperty("input").GetProperty("device").GetString()} "
                    + $"out={voiceState.GetProperty("output").GetProperty("device").GetString()}");

                // Speak 0.6 s, then 1.4 s of silence: one utterance, on a stream-like cadence.
                var source = components.LastSource!;
                for (var i = 0; i < 30; i++)
                {
                    source.Emit(0.5f, 320);
                    await Task.Delay(25);
                }

                // The reported level has to follow the microphone: a dead device, a quiet room and speech
                // under the threshold must be distinguishable on the settings page.
                var levelWhileSpeaking = 0.0;
                for (var attempt = 0; attempt < 200 && levelWhileSpeaking < 0.03; attempt++)
                {
                    var level = await loopHttp.GetFromJsonAsync<JsonElement>("/api/voice");
                    levelWhileSpeaking = level.GetProperty("input").GetProperty("rms").GetDouble();
                    await Task.Delay(25);
                }

                Check("voice: the reported input level rises while the microphone hears speech",
                    levelWhileSpeaking >= 0.03,
                    $"rms={levelWhileSpeaking:0.0000} threshold=0.03");

                for (var i = 0; i < 70; i++)
                {
                    source.Emit(0f, 320);
                    await Task.Delay(25);
                }

                var levelWhileQuiet = (await loopHttp.GetFromJsonAsync<JsonElement>("/api/voice"))
                    .GetProperty("input").GetProperty("rms").GetDouble();

                Check("voice: the reported input level falls back when the speaking stops",
                    levelWhileQuiet < 0.03,
                    $"rms={levelWhileQuiet:0.0000}");

                // Noise-floor probing must not treat speech that starts immediately as the floor: that reports
                // a false threshold misconfiguration.
                var loopInput = (await loopHttp.GetFromJsonAsync<JsonElement>("/api/voice")).GetProperty("input");
                Check("voice: speech at startup is not mistaken for the noise floor",
                    !loopInput.GetProperty("thresholdMisconfigured").GetBoolean(),
                    $"ambient={loopInput.GetProperty("ambientRms").GetDouble():0.0000} "
                    + $"misconfigured={loopInput.GetProperty("thresholdMisconfigured").GetBoolean()}");

                // Capture → queue → turn → session → speech.
                var answered = false;
                var spoken = false;
                for (var attempt = 0; attempt < 400 && !(answered && spoken); attempt++)
                {
                    if (!answered)
                    {
                        var session = await loopHttp.GetFromJsonAsync<JsonElement>("/api/session");
                        answered = session.GetProperty("items").EnumerateArray().Any(item =>
                            item.GetProperty("kind").GetString() == "Assistant"
                            && item.GetProperty("body").GetString()!.Contains("书房的灯", StringComparison.Ordinal));
                    }

                    spoken = components.TtsRequests.Count >= 2;
                    await Task.Delay(25);
                }

                Check("voice: an utterance is answered by a turn and the answer is spoken",
                    answered && spoken && components.LastSink!.Segments.Count > 0,
                    $"answered={answered} sentences={components.TtsRequests.Count} segments={components.LastSink?.Segments.Count ?? 0}");

                var loopQueue = await loopHttp.GetFromJsonAsync<JsonElement>("/api/queue");
                var inputState = await loopHttp.GetFromJsonAsync<JsonElement>("/api/voice");
                Check("voice: the utterance is consumed and the halves stay up",
                    loopQueue.GetProperty("depth").GetInt32() == 0
                    && inputState.GetProperty("input").GetProperty("count").GetInt32() == 1
                    && inputState.GetProperty("output").GetProperty("count").GetInt32() >= 2,
                    $"depth={loopQueue.GetProperty("depth").GetInt32()} "
                    + $"utterances={inputState.GetProperty("input").GetProperty("count").GetInt32()} "
                    + $"spoken={inputState.GetProperty("output").GetProperty("count").GetInt32()}");

                // A device that refuses to open must not take the other half down with it. The output section
                // changes (drainMs), because an unchanged section is deliberately not restarted.
                components.FailToOpenSink = true;
                var reenable = await loopHttp.PutAsJsonAsync("/api/settings", new
                {
                    revision = (await loopHttp.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("revision").GetInt64(),
                    input = new { enabled = true, utteranceGapMs = 200 },
                    output = new { enabled = true, drainMs = 123 },
                    asr = new { baseUrl = "http://127.0.0.1:45101", model = "smoke-asr" },
                    tts = new { baseUrl = "http://127.0.0.1:45102", lang = "zh" },
                });
                var afterFailure = (await reenable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("voice");

                Check("voice: a device that will not open is reported without stopping the other half",
                    afterFailure.GetProperty("input").GetProperty("running").GetBoolean()
                    && !afterFailure.GetProperty("output").GetProperty("running").GetBoolean()
                    && afterFailure.GetProperty("output").GetProperty("reason").GetString()!.Contains("打开输出设备失败", StringComparison.Ordinal),
                    afterFailure.GetProperty("output").GetProperty("reason").GetString() ?? "(none)");
            }
            finally
            {
                await loopApp.StopAsync();
                await loopRun;
                await loopVoice.DisposeAsync();
                await loopConsumer.DisposeAsync();
                await loopContext.DisposeAsync();
            }
        }

    }
}
