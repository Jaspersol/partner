using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Partner.Core.Agent.Delegation;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Media;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Scheduler;
using Partner.Core.Sessions;

namespace Partner.Core.Agent;

/// <summary>How much of the context window the history currently occupies.</summary>
public sealed record ContextUsage(
    int UsedTokens,
    int BudgetTokens,
    int ContextTokens,
    int MaxTokens,
    int KeptItems,
    int FoldedItems,
    int SystemTokens = 0,
    int SummaryTokens = 0)
{
    public double Fraction => BudgetTokens <= 0 ? 0 : (double)UsedTokens / BudgetTokens;
}

public sealed record InitialContext(IReadOnlyList<LlmMessage> Messages, string? Recall);

/// <summary>
/// Builds the messages a turn starts with:
/// <c>system (base + hook injections)</c> → <c>session window (trimmed to the token budget)</c> →
/// <c>current input</c>.
/// </summary>
public interface IContextAssembler
{
    Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        IReadOnlyList<string?>? injectedLabels = null,
        CancellationToken ct = default);

    async Task<InitialContext> AssemblePlannedAsync(
        InputItem item, IReadOnlyList<string> injectedPrompts, IReadOnlyList<string?>? injectedLabels = null,
        CancellationToken ct = default) =>
        new(await AssembleInitialAsync(item, injectedPrompts, injectedLabels, ct).ConfigureAwait(false), null);

    /// <summary>What the history costs right now, without a turn — the number a UI shows.</summary>
    Task<ContextUsage> MeasureAsync(CancellationToken ct = default);
}

/// <summary>
/// Default assembler: base system prompt + hook injections + the newest history that fits the model's context
/// window + the current input.
/// <para>
/// The window is a <b>token</b> budget, not an item count: what overflows a model is tokens, and one tool
/// result can outweigh twenty short turns. The budget reserves room for the completion.
/// </para>
/// </summary>
public sealed class SessionWindowContextAssembler : IContextAssembler
{
    private readonly ISessionStore _session;
    private readonly int _contextTokens;
    private readonly int _reserveForOutput;
    private readonly ITokenCounter _tokens;
    private readonly string _baseSystemPrompt;
    private readonly Func<string?>? _promptSource;
    private readonly IMediaStore? _media;
    private readonly Func<int>? _reservedTokens;
    private readonly IMemoryStore? _memory;
    private readonly IScheduler? _scheduler;

    public SessionWindowContextAssembler(
        ISessionStore session,
        int contextTokens = 32768,
        int reserveForOutput = 4096,
        ITokenCounter? tokens = null,
        string? baseSystemPrompt = null,
        IMediaStore? media = null,
        Func<string?>? promptSource = null,
        Func<int>? reservedTokens = null,
        IMemoryStore? memory = null,
        IScheduler? scheduler = null)
    {
        _reservedTokens = reservedTokens;
        _scheduler = scheduler;
        _memory = memory;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);

        _session = session ?? throw new ArgumentNullException(nameof(session));
        _contextTokens = contextTokens;
        _reserveForOutput = reserveForOutput;
        _tokens = tokens ?? new ApproximateTokenCounter();
        _baseSystemPrompt = baseSystemPrompt ?? AgentPrompts.MainDefault;
        _promptSource = promptSource;
        _media = media;
    }

    /// <summary>
    /// The prompt in force right now: the live source when it has something to say, else the fixed one.
    /// Read per call, never cached.
    /// </summary>
    private string BasePrompt =>
        _promptSource?.Invoke() is { } live && !string.IsNullOrWhiteSpace(live) ? live : _baseSystemPrompt;

    public async Task<ContextUsage> MeasureAsync(CancellationToken ct = default)
    {
        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var plan = await PlanAsync(view, BasePrompt, injectedPrompts: Array.Empty<string>(), currentInput: null, fromMemory: true, ct)
            .ConfigureAwait(false);

        return new ContextUsage(
            plan.SystemTokens + plan.TurnTokens,
            ContextBudget.PromptBudget(_contextTokens, _reserveForOutput),
            _contextTokens,
            _reserveForOutput,
            plan.Kept.Count,
            plan.FoldedCount,
            plan.SystemTokens,
            plan.SummaryTokens);
    }

    public async Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        IReadOnlyList<string?>? injectedLabels = null,
        CancellationToken ct = default) =>
        (await AssemblePlannedAsync(item, injectedPrompts, injectedLabels, ct).ConfigureAwait(false)).Messages;

    public async Task<InitialContext> AssemblePlannedAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        IReadOnlyList<string?>? injectedLabels = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(injectedPrompts);

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        if (item.Source == InputSource.Schedule)
        {
            var parts = item.IdempotencyKey.Split(':', 3);
            var taskId = parts.Length == 3 && parts[0] == "schedule" && parts[1].Length > 0 ? parts[1] : null;
            var state = new List<string>();
            if (taskId is not null && _scheduler is not null)
            {
                var task = (await _scheduler.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.TaskId == taskId);
                if (task?.LastRunAt is { } at)
                    state.Add($"任务 {taskId} 上次运行：{at:O}，运行结果：{(task.LastRunOk == true ? "完成" : "失败")}；错误：{task.LastRunError ?? "无"}。运行完成不等于业务检查通过。");
            }
            if (taskId is not null)
            {
                var resolved = new HashSet<string>(StringComparer.Ordinal);
                SessionViewItem? anomaly = null;
                SessionViewItem? failure = null;
                for (var i = view.Items.Count - 1; i >= 0; i--)
                {
                    var entry = view.Items[i];
                    var meta = entry.Meta;
                    if (entry.Kind != SessionEventKind.ToolResult || meta?.GetValueOrDefault("scheduleTaskId") != taskId)
                        continue;

                    var target = meta.GetValueOrDefault("checkTarget");
                    var status = 0;
                    var valid = meta.GetValueOrDefault("checkKind") == "http-status-v1"
                        && target is { Length: 64 } && target.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                        && int.TryParse(meta.GetValueOrDefault("checkStatus"), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out status)
                        && (status is >= 200 and <= 299 or >= 500 and <= 599);
                    if (valid)
                    {
                        if (meta.GetValueOrDefault("checkBatchMixed") != "true")
                        {
                            if (status < 300)
                                resolved.Add(target!);
                            else if (anomaly is null && !resolved.Contains(target!))
                                anomaly = entry;
                        }
                    }
                    else if (failure is null && meta.GetValueOrDefault("ok") == "false")
                        failure = entry;
                }
                if (anomaly is not null)
                    state.Add($"该任务未解决的 HTTP 检查异常 #{anomaly.Seq}：HTTP {anomaly.Meta!["checkStatus"]}，观测时间 {anomaly.At:O}。仅表示该次 HTTP 响应；重查目标来自当前任务指令，必须实际重新检查，以本轮工具结果作答。可按 Seq 回读可见的原始结果。");
                else if (failure is not null)
                    state.Add($"该任务最近的普通工具调用失败线索 #{failure.Seq}：{Trim(failure.Body)}。这不是业务状态；是否已解决尚未经验证，当前要求重查时必须实际重新检查，不能以旧线索代替检查。");
            }
            var scheduleView = new SessionView(view.LastSeq, Array.Empty<SessionViewItem>());
            view = scheduleView;
            if (state.Count > 0)
                injectedPrompts = injectedPrompts.Concat(state).ToArray();
        }
        var attachments = ResolveAttachments(item.MediaIds);
        var images = attachments.Where(a => a.IsImage).Select(a => a.ToDataUri()).ToList();
        var body = MessageBody(FrameBySource(item.Source, item.Payload, item.EnqueuedAt), attachments);
        var basePrompt = BasePrompt;
        var plan = await PlanAsync(view, basePrompt, injectedPrompts, body, fromMemory: false, ct, images.Count)
            .ConfigureAwait(false);

        // Split by how often a block changes. Stable blocks stay in the system message; the volatile recall block
        // changes nearly every turn, and anything that differs at the start of the prompt invalidates the prefix
        // cache for the whole history behind it. It goes after the history, in front of the current input.
        var stable = new List<string>();
        var volatileBlocks = new List<string>();
        for (var i = 0; i < injectedPrompts.Count; i++)
        {
            var label = injectedLabels is not null && i < injectedLabels.Count ? injectedLabels[i] : null;
            (string.Equals(label, VolatileLabel, StringComparison.Ordinal) ? volatileBlocks : stable).Add(injectedPrompts[i]);
        }

        string? recall = null;
        var remaining = ContextBudget.PromptBudget(_contextTokens, _reserveForOutput) - plan.SystemTokens - plan.TurnTokens - plan.InputTokens;
        if (item.Source != InputSource.Schedule && _memory is not null && remaining > 0 && !string.IsNullOrWhiteSpace(item.Payload))
        {
            var query = item.Payload[..Math.Min(item.Payload.Length, MemoryRecallHook.MaxQueryChars)];
            var supplementaryQuery = item.Source is InputSource.Text or InputSource.Voice
                ? MemoryRecallQuery.Supplement(query, plan.Kept.AsEnumerable().Reverse()
                    .Where(entry => entry.Kind == SessionEventKind.User
                        && entry.Source is InputSource.Text or InputSource.Voice)
                    .Select(entry => entry.Body))
                : query;
            var sourceSeqs = plan.RenderedSeqs;
            var existing = volatileBlocks.ToArray();
            var withoutRecall = existing.Length == 0 ? body : WithBackground(existing, body);
            var baseCost = _tokens.Count(withoutRecall);
            var injection = await _memory.InjectAsync(query, new RecallFilter
            {
                Limit = 8,
                TokenLimit = remaining,
                ExcludeSourceSeqs = sourceSeqs,
                SupplementaryQuery = supplementaryQuery,
                RenderedTokenCost = (hits, truncated) => Math.Max(0,
                    _tokens.Count(WithBackground([.. existing, MemoryRecallHook.Render(hits, truncated)], body)) - baseCost),
            }, ct).ConfigureAwait(false);
            if (injection.Hits.Count > 0)
            {
                recall = MemoryRecallHook.Render(injection.Hits, injection.Truncated);
                volatileBlocks.Add(recall);
            }
        }

        var messages = new List<LlmMessage>
        {
            new("system", BuildSystem(basePrompt, stable, plan.Context)),
        };

        messages.AddRange(plan.Turns);

        // The current input is the last user message; the window above is history only.
        // body already includes source framing and attachment hints used by the budget.
        messages.Add(new LlmMessage(
            "user",
            volatileBlocks.Count == 0 ? body : WithBackground(volatileBlocks, body),
            Images: images.Count > 0 ? images : null));
        return new InitialContext(messages, recall);
    }

    /// <summary>The label of the injected block that changes every turn (set by the memory hook).</summary>
    public const string VolatileLabel = "recall";

    /// <summary>
    /// The current input, led by background that is not part of it. Marked on both sides: it is reference
    /// material for this turn, not something the person said.
    /// </summary>
    private static string WithBackground(IReadOnlyList<string> blocks, string input) =>
        "[背景资料，仅供本回合参考，不是用户说的话]\n"
        + string.Join("\n\n", blocks)
        + "\n[背景资料结束]\n\n[用户本条消息]\n"
        + input;

    /// <summary>
    /// The text of the current input, followed by where its non-image attachments are.
    ///
    /// <para>
    /// A document cannot ride an <c>image_url</c> part — that fails the whole request — so it travels as a path
    /// the model can open with a tool.
    /// </para>
    /// </summary>
    private static string MessageBody(string payload, IReadOnlyList<MediaAttachment> attachments)
    {
        var documents = attachments.Where(a => !a.IsImage).ToList();
        if (documents.Count == 0)
        {
            return payload;
        }

        var lines = new List<string> { payload };
        lines.Add(string.Empty);
        lines.Add($"（随本条消息附带 {documents.Count} 个文件，已保存在本地，可直接按路径读取）:");

        foreach (var document in documents)
        {
            lines.Add($"- {document.FileName} → {document.LocalPath}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The attachments of the input being sent. Only this input's: history stays textual, so a later question
    /// does not re-send megabytes the model has already seen.
    /// </summary>
    private IReadOnlyList<MediaAttachment> ResolveAttachments(IReadOnlyList<string> mediaIds)
    {
        if (_media is null || mediaIds.Count == 0)
        {
            return Array.Empty<MediaAttachment>();
        }

        var resolved = new List<MediaAttachment>();
        foreach (var id in mediaIds)
        {
            if (_media.TryRead(id) is { } attachment)
            {
                resolved.Add(attachment);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Everything the two public entry points share: what fits, and what it costs.
    /// <para>
    /// <paramref name="fromMemory"/> keeps the read-only measurement read-only: reporting occupancy must not wait
    /// on a model endpoint that is slow, dead, or not configured yet.
    /// </para>
    /// </summary>
    private async Task<Plan> PlanAsync(
        SessionView view,
        string basePrompt,
        IReadOnlyList<string> injectedPrompts,
        string? currentInput,
        bool fromMemory,
        CancellationToken ct,
        int imageCount = 0)
    {
        // A summary stands in for history that was folded away, so it is context rather than a turn.
        var summaries = view.Items.Where(entry => entry.Kind == SessionEventKind.Summary).ToList();
        var conversation = ReplayableHistory(view.Items);

        ValueTask<int> Cost(string? text, CancellationToken token) => fromMemory
            ? ValueTask.FromResult(_tokens.Count(text ?? string.Empty) + ContextBudget.MessageOverheadTokens)
            : ContextBudget.CostAsync(_tokens, text, token);

        var context = summaries.Select(summary => $"summary of earlier history: {summary.Body}").ToList();

        // Price the actual system block, including the summary wrapper and separators.
        var systemTokens = await Cost(BuildSystem(basePrompt, injectedPrompts, context), ct).ConfigureAwait(false);
        var summaryTokens = context.Count == 0 ? 0 : Math.Max(0,
            _tokens.Count(BuildSystem(basePrompt, injectedPrompts, context))
            - _tokens.Count(BuildSystem(basePrompt, injectedPrompts, Array.Empty<string>())));
        systemTokens += _reservedTokens?.Invoke() ?? 0;

        var inputTokens = currentInput is null
            ? 0
            : await Cost(currentInput, ct).ConfigureAwait(false);

        // An attachment costs the window something a character count cannot see. The flat per-image charge keeps
        // the budget from claiming room the image will actually take.
        inputTokens += imageCount * ContextBudget.ImageTokens;

        var budget = ContextBudget.PromptBudget(_contextTokens, _reserveForOutput) - systemTokens - inputTokens;

        // Priced once: a model-backed counter would otherwise make a second round trip per kept entry.
        var priced = new Dictionary<SessionViewItem, int>(ReferenceEqualityComparer.Instance);

        async ValueTask<int> CostOf(SessionViewItem entry, CancellationToken token)
        {
            if (!priced.TryGetValue(entry, out var cost))
            {
                priced[entry] = cost = await Cost(CostText(entry), token).ConfigureAwait(false);
            }

            return cost;
        }

        var kept = await ContextBudget.FitNewestGroupsAsync(
            conversation,
            (entry, token) => CostOf(entry, token),
            Math.Max(0, budget),
            ct).ConfigureAwait(false);
        kept = ReplayableHistory(kept);

        // Entries that are not conversational are still shown to the model, just inside the system block.
        foreach (var entry in kept.Where(entry =>
            entry.Kind is not (SessionEventKind.User or SessionEventKind.Assistant or SessionEventKind.Schedule)
            && !IsHandoffTrace(entry)))
        {
            context.Add($"#{entry.Seq} [{entry.Kind}] {entry.Body}");
        }

        var turns = BuildTurns(kept);

        var turnTokens = 0;
        foreach (var entry in kept)
        {
            turnTokens += await CostOf(entry, ct).ConfigureAwait(false);
        }


        var renderedSeqs = summaries.Select(e => e.Seq).Concat(kept.Select(e => e.Seq)).ToHashSet();
        return new Plan(
            context,
            turns,
            kept,
            systemTokens,
            turnTokens,
            conversation.Count - kept.Count,
            inputTokens,
            renderedSeqs,
            summaryTokens);
    }

    /// <summary>The most of a hand-off's arguments or result that is replayed. The detail is in the log.</summary>
    private const int TraceChars = 400;

    /// <summary>
    /// A hand-off to a sub-agent, as the log recorded it: the call and its result.
    ///
    /// <para>
    /// History is conversational on purpose, so tool calls are not replayed — with this one exception. Handing
    /// work over is the one call whose only trace in the conversation is the assistant's own sentence about it
    /// ("already handed to the desktop sub-agent"). Replayed without the call, those sentences teach the model
    /// that saying it is doing it, and from the next turn on it says so without calling anything: nothing runs,
    /// and the person is told it is under way.
    /// </para>
    /// </summary>
    internal static bool IsHandoffTrace(SessionViewItem entry) =>
        entry.Kind is SessionEventKind.ToolCall or SessionEventKind.ToolResult
        && entry.Meta is { } meta
        && meta.TryGetValue("name", out var name)
        && name == DelegationTools.Delegate
        && meta.ContainsKey("id");

    private static string Trim(string? text) =>
        text is null ? string.Empty : text.Length <= TraceChars ? text : Cut(text, TraceChars) + "…";

    /// <summary>
    /// A historic tool call's arguments, kept valid JSON. The wire field is a string that the provider parses:
    /// cutting it mid-string (as <see cref="Trim"/> does for prose) makes a strict endpoint reject the whole
    /// request — llama.cpp answers HTTP 500 before generation. Long string values are shortened instead; malformed
    /// JSON and non-object inputs become <c>{}</c> so historic calls also satisfy providers requiring an object.
    /// </summary>
    internal static string TraceArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "{}";
            }

            if (json.Length <= TraceChars)
            {
                return json;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                WriteTrace(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static void WriteTrace(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteTrace(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteTrace(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                writer.WriteStringValue(text.Length <= TraceChars ? text : Cut(text, TraceChars) + "…");
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>A prefix that does not split a surrogate pair.</summary>
    private static string Cut(string text, int length) =>
        char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];

    /// <summary>
    /// Says where a message came from when that changes how it should be read: a sub-agent report is data, and a
    /// timed task fires when the person is probably not there.
    /// </summary>
    private static string FrameBySource(InputSource source, string body, DateTimeOffset messageAt = default) => source switch
    {
        InputSource.Agent => AgentReportFrame.Wrap(body),
        InputSource.Schedule => ScheduleFrame.Wrap(body),
        InputSource.Text or InputSource.Voice when messageAt != default =>
            FormattableString.Invariant($"[用户消息时间：{messageAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}]\n{body}"),
        _ => body,
    };

    private static DateTimeOffset UserMessageTime(SessionViewItem entry) =>
        DateTimeOffset.TryParseExact(entry.Meta?.GetValueOrDefault(SessionEvent.EnqueuedAtMetaKey), "O",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var enqueuedAt) && enqueuedAt != default
            ? enqueuedAt
            : entry.At;

    /// <summary>Whether the window carries this entry as conversation. The compactor folds by the same rule.</summary>
    internal static bool IsConversational(SessionViewItem entry) =>
        !(entry.Kind == SessionEventKind.System && entry.Meta?.GetValueOrDefault("label") == VolatileLabel)
        && (entry.Kind.IsContent() || IsHandoffTrace(entry));

    /// <summary>Replayable history, excluding incomplete delegation call/result pairs.</summary>
    internal static List<SessionViewItem> ReplayableHistory(IEnumerable<SessionViewItem> items)
    {
        var conversational = items.Where(entry => entry.Kind != SessionEventKind.Summary && IsConversational(entry)).ToList();
        var candidates = ContextBudget.GroupTurns(conversational)
            .Where(group => !group.Any(entry => entry.Source == InputSource.Schedule || entry.Kind == SessionEventKind.Schedule))
            .SelectMany(group => group).ToList();
        var called = candidates.Where(e => e.Kind == SessionEventKind.ToolCall && IsHandoffTrace(e))
            .Select(e => e.Meta!["id"]).ToHashSet();
        var answered = candidates.Where(e => e.Kind == SessionEventKind.ToolResult && IsHandoffTrace(e))
            .Select(e => e.Meta!["id"]).ToHashSet();
        return candidates.Where(e => !IsHandoffTrace(e)
            || (called.Contains(e.Meta!["id"]) && answered.Contains(e.Meta["id"]))).ToList();
    }

    /// <summary>Text actually rendered for a replayable history entry.</summary>
    internal static string CostText(SessionViewItem entry) => entry.Kind switch
    {
        SessionEventKind.User => FrameBySource(entry.Source, entry.Body, UserMessageTime(entry)),
        SessionEventKind.Schedule => FrameBySource(entry.Source, entry.Body),
        SessionEventKind.Assistant => entry.Body,
        SessionEventKind.ToolCall when IsHandoffTrace(entry) => TraceArguments(entry.Meta!.GetValueOrDefault("args")),
        SessionEventKind.ToolResult when IsHandoffTrace(entry) => Trim(entry.Body),
        _ => $"#{entry.Seq} [{entry.Kind}] {entry.Body}",
    };

    /// <summary>
    /// The window as messages. A hand-off is replayed in the shape the model produced it — the assistant message
    /// carries the call, a tool message carries the result — and only when both survived the budget: a call with
    /// no result, or a result with no call, is rejected by the provider.
    /// </summary>
    private static List<LlmMessage> BuildTurns(IReadOnlyList<SessionViewItem> kept)
    {
        var called = kept.Where(e => e.Kind == SessionEventKind.ToolCall && IsHandoffTrace(e)).Select(e => e.Meta!["id"]).ToHashSet();
        var answered = kept.Where(e => e.Kind == SessionEventKind.ToolResult && IsHandoffTrace(e)).Select(e => e.Meta!["id"]).ToHashSet();
        var complete = called.Intersect(answered).ToHashSet();

        var turns = new List<LlmMessage>();
        var openCalls = false;

        foreach (var entry in kept)
        {
            if (IsHandoffTrace(entry))
            {
                var id = entry.Meta!["id"];
                if (!complete.Contains(id))
                {
                    continue;
                }

                if (entry.Kind == SessionEventKind.ToolCall)
                {
                    var call = new LlmToolCall(id, DelegationTools.Delegate, TraceArguments(entry.Meta!.GetValueOrDefault("args")));
                    var last = turns.Count > 0 ? turns[^1] : null;

                    if (openCalls && last is { Role: "assistant", ToolCalls: { } calls })
                    {
                        turns[^1] = last with { ToolCalls = [.. calls, call] };
                    }
                    else if (last is { Role: "assistant", ToolCalls: null })
                    {
                        turns[^1] = last with { ToolCalls = [call] };
                    }
                    else
                    {
                        turns.Add(new LlmMessage("assistant", null, [call]));
                    }

                    openCalls = true;
                }
                else
                {
                    turns.Add(new LlmMessage("tool", Trim(entry.Body), ToolCallId: id, Name: DelegationTools.Delegate));
                    openCalls = false;
                }

                continue;
            }

            if (entry.Kind is not (SessionEventKind.User or SessionEventKind.Assistant or SessionEventKind.Schedule))
            {
                continue;
            }

            openCalls = false;
            turns.Add(entry.Kind == SessionEventKind.Assistant
                ? new LlmMessage("assistant", entry.Body)
                : new LlmMessage("user", CostText(entry)));
        }

        return turns;
    }

    private static string BuildSystem(string basePrompt, IReadOnlyList<string> injectedPrompts, IReadOnlyList<string> context)
    {
        var blocks = new List<string> { basePrompt };
        blocks.AddRange(injectedPrompts);

        if (context.Count > 0)
        {
            blocks.Add("Earlier activity in this session:\n" + string.Join("\n", context));
        }

        return string.Join("\n\n", blocks);
    }

    private sealed record Plan(
        List<string> Context,
        List<LlmMessage> Turns,
        List<SessionViewItem> Kept,
        int SystemTokens,
        int TurnTokens,
        int FoldedCount,
        int InputTokens,
        IReadOnlySet<long> RenderedSeqs,
        int SummaryTokens);
}
