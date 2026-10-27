using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Partner.Core.Agent.Delegation;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Media;
using Partner.Core.Memory;
using Partner.Core.Queue;
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
    int SystemTokens = 0)
{
    public double Fraction => BudgetTokens <= 0 ? 0 : (double)UsedTokens / BudgetTokens;
}

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

    public SessionWindowContextAssembler(
        ISessionStore session,
        int contextTokens = 32768,
        int reserveForOutput = 4096,
        ITokenCounter? tokens = null,
        string? baseSystemPrompt = null,
        IMediaStore? media = null,
        Func<string?>? promptSource = null,
        Func<int>? reservedTokens = null)
    {
        _reservedTokens = reservedTokens;
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
        var plan = await PlanAsync(view, injectedPrompts: Array.Empty<string>(), currentInput: null, fromMemory: true, ct)
            .ConfigureAwait(false);

        return new ContextUsage(
            plan.SystemTokens + plan.TurnTokens,
            ContextBudget.PromptBudget(_contextTokens, _reserveForOutput),
            _contextTokens,
            _reserveForOutput,
            plan.Kept.Count,
            plan.FoldedCount,
            plan.SystemTokens);
    }

    public async Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        IReadOnlyList<string?>? injectedLabels = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(injectedPrompts);

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var attachments = ResolveAttachments(item.MediaIds);
        var images = attachments.Where(a => a.IsImage).Select(a => a.ToDataUri()).ToList();
        var plan = await PlanAsync(view, injectedPrompts, item.Payload, fromMemory: false, ct, images.Count)
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

        var messages = new List<LlmMessage>
        {
            new("system", BuildSystem(BasePrompt, stable, plan.Context)),
        };

        messages.AddRange(plan.Turns);

        // The current input is the last user message; the window above is history only.
        var body = MessageBody(FrameBySource(item.Source, item.Payload), attachments);
        messages.Add(new LlmMessage(
            "user",
            volatileBlocks.Count == 0 ? body : WithBackground(volatileBlocks, body),
            Images: images.Count > 0 ? images : null));
        return messages;
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
        IReadOnlyList<string> injectedPrompts,
        string? currentInput,
        bool fromMemory,
        CancellationToken ct,
        int imageCount = 0)
    {
        // A summary stands in for history that was folded away, so it is context rather than a turn.
        var summaries = view.Items.Where(entry => entry.Kind == SessionEventKind.Summary).ToList();
        var conversation = view.Items
            .Where(entry => entry.Kind != SessionEventKind.Summary && (entry.Kind.IsContent() || IsHandoffTrace(entry)))
            .ToList();

        ValueTask<int> Cost(string? text, CancellationToken token) => fromMemory
            ? ValueTask.FromResult(_tokens.Count(text ?? string.Empty) + ContextBudget.MessageOverheadTokens)
            : ContextBudget.CostAsync(_tokens, text, token);

        var context = summaries.Select(summary => $"summary of earlier history: {summary.Body}").ToList();

        // The system block is always sent: dropping it would remove the instructions the turn runs under.
        var systemTokens = await Cost(BasePrompt, ct).ConfigureAwait(false);

        // The tool definitions are sent with every request and never appear in the log, so they come off the top too.
        systemTokens += _reservedTokens?.Invoke() ?? 0;

        foreach (var prompt in injectedPrompts)
        {
            systemTokens += await Cost(prompt, ct).ConfigureAwait(false);
        }

        foreach (var entry in context)
        {
            systemTokens += await Cost(entry, ct).ConfigureAwait(false);
        }

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

        var kept = await ContextBudget.FitNewestFirstAsync(
            conversation,
            (entry, token) => CostOf(entry, token),
            Math.Max(0, budget),
            ct).ConfigureAwait(false);

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

        return new Plan(
            context,
            turns,
            kept,
            systemTokens,
            turnTokens,
            conversation.Count - kept.Count);
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
    /// request — llama.cpp answers HTTP 500 before generation. Long string values are shortened instead, and an
    /// already-unparseable entry becomes <c>{}</c> so one bad turn cannot wedge every later turn.
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
    internal static string FrameBySource(InputSource source, string body) => source switch
    {
        InputSource.Agent => AgentReportFrame.Wrap(body),
        InputSource.Schedule => ScheduleFrame.Wrap(body),
        _ => body,
    };

    /// <summary>Whether the window carries this entry as conversation. The compactor folds by the same rule.</summary>
    internal static bool IsConversational(SessionViewItem entry) => entry.Kind.IsContent() || IsHandoffTrace(entry);

    /// <summary>What an entry costs in the window: its text, and for a hand-off call its arguments too.</summary>
    internal static string CostText(SessionViewItem entry) =>
        entry.Kind == SessionEventKind.ToolCall && entry.Meta is { } meta
            ? entry.Body + TraceArguments(meta.GetValueOrDefault("args"))
            : IsHandoffTrace(entry) ? Trim(entry.Body) : entry.Body;

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
                : new LlmMessage("user", FrameBySource(entry.Source, entry.Body)));
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
        int FoldedCount);
}
