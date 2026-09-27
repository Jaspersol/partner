using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Agent;

public sealed record AgentTurnOptions
{
    /// <summary>Hard cap on steps inside one turn (a budget, not a semantic limit).</summary>
    public int MaxSteps { get; init; } = 8;

    public int MaxTokens { get; init; } = 4096;

    /// <summary>Upper bound on concurrently dispatched tool calls (design doc §2.9.5: bounded rolling pool).</summary>
    public int MaxParallelToolCalls { get; init; } = 4;

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The model's context window in tokens; the prompt budget is this minus <see cref="MaxTokens"/>.</summary>
    public int ContextWindowTokens { get; init; } = 32768;

    public string? BaseSystemPrompt { get; init; }
}

public sealed record TurnResult(
    string Reason,
    int Steps,
    int AssistantMessages,
    int ToolCalls,
    long TurnStartSeq,
    long TurnEndSeq);

/// <summary>
/// Runs one turn for one already-claimed <see cref="InputItem"/> (design doc §2.9).
/// <para>
/// Invariants enforced here:
/// <list type="bullet">
///   <item>Event order is exactly <c>turn/start → step/start → user → system → assistant → tool/call → tool/result → step/end → turn/end</c>.</item>
///   <item>Every turn ends with a <c>turn/end</c> carrying a reason — including cancellation and error paths.</item>
///   <item>Tool results commit in <b>model order</b>, never completion order.</item>
///   <item>Tool failures and timeouts are fed back to the model as results; they do not abort the turn.</item>
///   <item>Hooks run in phase order; <c>Finally</c> always runs.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AgentTurnRunner
{
    private readonly ISessionStore _session;
    private readonly ILlmClient _llm;
    private readonly IToolRegistry _tools;
    private readonly IToolGovernance _governance;
    private readonly IApprovalHandler _approval;
    private readonly IHookRegistry _hooks;
    private readonly IContextAssembler _assembler;
    private readonly AgentTurnOptions _options;
    private readonly IAssistantStream? _stream;

    public AgentTurnRunner(
        ISessionStore session,
        ILlmClient llm,
        IToolRegistry tools,
        IToolGovernance governance,
        IApprovalHandler approval,
        IHookRegistry hooks,
        IContextAssembler? assembler = null,
        AgentTurnOptions? options = null,
        IAssistantStream? stream = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _governance = governance ?? throw new ArgumentNullException(nameof(governance));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _options = options ?? new AgentTurnOptions();
        _stream = stream;
        _assembler = assembler ?? new SessionWindowContextAssembler(
            session, _options.ContextWindowTokens, _options.MaxTokens, baseSystemPrompt: _options.BaseSystemPrompt);
    }

    public async Task<TurnResult> RunAsync(InputItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The queue owns claiming (design doc §2.9.2): a turn may only run an item the queue handed out.
        if (item.Status != InputItemStatus.Running)
        {
            throw new InvalidOperationException(
                $"Turn requires a claimed (Running) item; got '{item.Status}'. Take it from the input queue first.");
        }

        _hooks.ResetTurn();

        var ctx = new HookContext
        {
            SessionId = "main",
            Source = item.Source,
            InputPayload = item.Payload,
            WorkspaceDirectory = Environment.CurrentDirectory,
        };

        var turnStartSeq = await _session.AppendAsync(Event(SessionEventKind.TurnStart, item.Source, "turn", item), ct)
            .ConfigureAwait(false);

        var steps = 0;
        var assistantMessages = 0;
        var toolCalls = 0;
        var reason = TurnEndReason.Error;
        var turnEnded = false;

        try
        {
            // Phases 1..5 run once per turn, in order. PostDispatch is unconditional because this build has
            // no slash-command dispatch yet; a hook may ShortCircuit to skip the agent entirely.
            await RunPhaseAsync(AgentPhase.PreDispatch, ctx, ct).ConfigureAwait(false);
            await RunPhaseAsync(AgentPhase.PostDispatch, ctx, ct).ConfigureAwait(false);
            await RunPhaseAsync(AgentPhase.PreAgentBuild, ctx, ct).ConfigureAwait(false);
            await RunPhaseAsync(AgentPhase.PostAgentBuild, ctx, ct).ConfigureAwait(false);
            var preExecute = await RunPhaseAsync(AgentPhase.PreExecute, ctx, ct).ConfigureAwait(false);

            // ShortCircuit ends the turn now; SkipAgent (sticky) skips the agent steps entirely.
            if (preExecute == HookOutcome.ShortCircuit || _hooks.SkipAgent)
            {
                reason = TurnEndReason.Completed;
                ctx.Extras["turn.detail"] = preExecute == HookOutcome.ShortCircuit ? "short-circuit" : "skip-agent";
            }
            else
            {
                var conversation = (await _assembler
                    .AssembleInitialAsync(item, ctx.InjectedPrompts, ct)
                    .ConfigureAwait(false)).ToList();

                var userLogged = false;
                var systemLogged = false;

                while (steps < _options.MaxSteps)
                {
                    ct.ThrowIfCancellationRequested();

                    steps++;
                    await _session.AppendAsync(Event(SessionEventKind.StepStart, item.Source, $"step {steps}", item), ct)
                        .ConfigureAwait(false);

                    if (!userLogged)
                    {
                        await _session.AppendAsync(Event(SessionEventKind.User, item.Source, item.Payload, item), ct)
                            .ConfigureAwait(false);
                        userLogged = true;
                    }

                    if (!systemLogged && ctx.InjectedPrompts.Count > 0)
                    {
                        await _session.AppendAsync(
                            Event(SessionEventKind.System, item.Source, string.Join(" | ", ctx.InjectedPrompts), item), ct)
                            .ConfigureAwait(false);
                        systemLogged = true;
                    }

                    var request = new LlmRequest(conversation, _tools.Specs, _options.MaxTokens);
                    var streamed = _stream is null
                        ? null
                        : new Action<string>(delta => _stream.OnAssistantDelta(item.ItemId, delta));

                    var response = streamed is null
                        ? await _llm.CompleteAsync(request, ct).ConfigureAwait(false)
                        : await _llm.StreamAsync(request, streamed, ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(response.Text))
                    {
                        assistantMessages++;
                        await _session.AppendAsync(
                            Event(SessionEventKind.Assistant, item.Source, response.Text!, item), ct)
                            .ConfigureAwait(false);
                        conversation.Add(new LlmMessage("assistant", response.Text, response.ToolCalls));
                        _stream?.OnAssistantCompleted(item.ItemId);
                    }
                    else if (response.ToolCalls.Count > 0)
                    {
                        conversation.Add(new LlmMessage("assistant", null, response.ToolCalls));
                    }

                    if (response.ToolCalls.Count > 0)
                    {
                        toolCalls += response.ToolCalls.Count;

                        foreach (var call in response.ToolCalls)
                        {
                            await _session.AppendAsync(
                                Event(SessionEventKind.ToolCall, item.Source, call.Name, item, new Dictionary<string, string>
                                {
                                    ["id"] = call.Id,
                                    ["name"] = call.Name,
                                    ["args"] = call.ArgumentsJson,
                                }), ct).ConfigureAwait(false);
                        }

                        var results = await ExecuteToolsAsync(response.ToolCalls, ct).ConfigureAwait(false);

                        for (var i = 0; i < response.ToolCalls.Count; i++)
                        {
                            var call = response.ToolCalls[i];
                            var result = results[i];

                            await _session.AppendAsync(
                                Event(SessionEventKind.ToolResult, item.Source, result.Content, item, new Dictionary<string, string>
                                {
                                    ["id"] = call.Id,
                                    ["name"] = call.Name,
                                    ["ok"] = result.Ok ? "true" : "false",
                                }), ct).ConfigureAwait(false);

                            conversation.Add(new LlmMessage("tool", result.Content, ToolCallId: call.Id, Name: call.Name));
                        }
                    }

                    await _session.AppendAsync(Event(SessionEventKind.StepEnd, item.Source, $"step {steps}", item), ct)
                        .ConfigureAwait(false);

                    if (response.ToolCalls.Count == 0)
                    {
                        reason = response.FinishReason == "length" ? TurnEndReason.MaxTokens : TurnEndReason.Completed;
                        break;
                    }

                    if (response.FinishReason == "length")
                    {
                        reason = TurnEndReason.MaxTokens;
                        break;
                    }
                }

                if (steps >= _options.MaxSteps)
                {
                    // Budget exhausted with work still pending: still a budget reason, tagged for diagnosis.
                    reason = TurnEndReason.MaxTokens;
                    ctx.Extras["turn.detail"] = "step-limit";
                }

                await RunPhaseAsync(AgentPhase.PostResponse, ctx, ct).ConfigureAwait(false);
            }

            var endMeta = new Dictionary<string, string> { ["reason"] = reason };
            if (ctx.Extras.TryGetValue("turn.detail", out var detail))
            {
                endMeta["detail"] = detail;
            }

            var turnEndSeq = await _session.AppendAsync(
                Event(SessionEventKind.TurnEnd, item.Source, $"turn {reason}", item, endMeta), ct).ConfigureAwait(false);
            turnEnded = true;

            // Durability barrier for the whole turn (design doc §2.9.7): a turn that cannot be committed
            // must not be reported as successful, regardless of the store's append mode.
            await _session.FlushAsync(ct).ConfigureAwait(false);

            return new TurnResult(reason, steps, assistantMessages, toolCalls, turnStartSeq, turnEndSeq);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!turnEnded)
            {
                await AppendTurnEndBestEffortAsync(item, TurnEndReason.Aborted, ctx, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            ctx.Error = ex.Message;
            await RunPhaseAsync(AgentPhase.OnError, ctx, CancellationToken.None).ConfigureAwait(false);

            if (!turnEnded)
            {
                // The error text goes into the session so the failure is visible (design doc §2.9.1).
                // Both appends are best-effort: a faulted store must not mask the original exception.
                try
                {
                    await _session.AppendAsync(
                        Event(SessionEventKind.System, item.Source, $"error: {ex.Message}", item),
                        CancellationToken.None).ConfigureAwait(false);
                    await AppendTurnEndBestEffortAsync(item, TurnEndReason.Error, ctx, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception appendFailure)
                {
                    Console.Error.WriteLine($"turn error path could not be recorded: {appendFailure.Message}");
                }
            }

            throw;
        }
        finally
        {
            // A turn that ends any other way (error, cancel, no text at all) must not leave a live fragment
            // on screen: the log is the record, and it has nothing to show.
            _stream?.OnAssistantCompleted(item.ItemId);
            await RunPhaseAsync(AgentPhase.Finally, ctx, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task AppendTurnEndBestEffortAsync(
        InputItem item,
        string reason,
        HookContext ctx,
        CancellationToken ct)
    {
        var meta = new Dictionary<string, string> { ["reason"] = reason };
        if (ctx.Extras.TryGetValue("turn.detail", out var detail))
        {
            meta["detail"] = detail;
        }

        await _session.AppendAsync(Event(SessionEventKind.TurnEnd, item.Source, $"turn {reason}", item, meta), ct)
            .ConfigureAwait(false);
    }

    private async Task<HookOutcome> RunPhaseAsync(AgentPhase phase, HookContext ctx, CancellationToken ct)
    {
        return await _hooks.RunAsync(phase, ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches all tool calls with bounded parallelism, then returns results in <b>model order</b>.
    /// Governance runs before every dispatch: deny is a result, not an exception (design doc §2.9.6).
    /// </summary>
    private async Task<IReadOnlyList<ToolResult>> ExecuteToolsAsync(
        IReadOnlyList<LlmToolCall> calls,
        CancellationToken ct)
    {
        var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelToolCalls));
        var tasks = new Task<ToolResult>[calls.Count];

        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            tasks[i] = Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await ExecuteOneAsync(call, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation never fabricates success (design doc §2.9.5).
            return calls.Select(c => new ToolResult(false, $"canceled before completion: {c.Name}")).ToList();
        }
        finally
        {
            gate.Dispose();
        }

        // Read in model order — completion order is irrelevant.
        var results = new List<ToolResult>(calls.Count);
        for (var i = 0; i < tasks.Length; i++)
        {
            results.Add(tasks[i].IsCompletedSuccessfully
                ? tasks[i].Result
                : new ToolResult(false, $"no result for {calls[i].Name}"));
        }

        return results;
    }

    private async Task<ToolResult> ExecuteOneAsync(LlmToolCall call, CancellationToken ct)
    {
        var tool = _tools.Find(call.Name);
        if (tool is null)
        {
            return new ToolResult(false, $"unknown tool: {call.Name}");
        }

        var decision = _governance.Decide(call.Name, call.ArgumentsJson);
        switch (decision.Decision)
        {
            case ToolDecision.Deny:
                return new ToolResult(false, $"denied: {decision.Reason}");

            case ToolDecision.Ask:
                var approved = await _approval.RequestAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
                if (!approved)
                {
                    return new ToolResult(false, $"approval denied: {decision.Reason}");
                }

                break;

            case ToolDecision.Allow:
            default:
                break;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ToolTimeout);

        try
        {
            return await tool.InvokeAsync(call.ArgumentsJson, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-tool timeout: fed back as a result, the turn continues.
            return new ToolResult(false, $"timeout after {_options.ToolTimeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"error: {ex.Message}");
        }
    }

    private static SessionEvent Event(
        SessionEventKind kind,
        InputSource source,
        string body,
        InputItem item,
        IReadOnlyDictionary<string, string>? meta = null)
    {
        var merged = new Dictionary<string, string>
        {
            ["itemId"] = item.ItemId,
            ["itemSeq"] = item.Seq.ToString(),
        };

        if (meta is not null)
        {
            foreach (var pair in meta)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return new SessionEvent
        {
            Kind = kind,
            Source = source,
            // Verbatim, newlines included: a reply is markdown, and its structure *is* the line breaks. Framing
            // is the serialized record's problem, and the serializer escapes them.
            Body = body,
            Meta = merged,
        };
    }
}
