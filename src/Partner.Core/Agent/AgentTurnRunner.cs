using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Agent;

public sealed record AgentTurnOptions
{
    /// <summary>Hard cap on steps inside one turn (a budget, not a semantic limit).
    /// <para>The floor every family budget stands on; see <see cref="ToolStepBudgets"/>.</para></summary>
    public int MaxSteps { get; init; } = 8;

    /// <summary>
    /// Step budgets beyond <see cref="MaxSteps"/>, by tool-name prefix.
    /// <para>
    /// One number for every family says a GUI task and a “what time is it” need the same room. They don’t:
    /// a real desktop task runs 20–40 steps of look, act, look — while a chat turn ends as soon as the answer
    /// is written either way, so a larger ceiling only binds premises that were never going to finish. Keys
    /// are prefixes applied to tool names: <c>desktop</c> covers every <c>desktop_*</c> tool,
    /// <c>mcp_</c> covers the server-bridged ones.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, int> ToolStepBudgets { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["desktop"] = 32, ["mcp_"] = 32, ["file_"] = 24 };

    /// <summary>
    /// The budget this turn runs under: the largest family budget among the tools <b>registered</b>, or
    /// <see cref="MaxSteps"/> when none of the keys matches.
    /// <para>
    /// Keyed on the registry, not on what the model has called so far. Extending the budget only after a
    /// family tool actually ran would mean a desktop task that spends its first few steps reading the
    /// desktop dies on the old number before its first action; and a registration is a statement about what
    /// kind of work this toolset is for all the same.
    /// </para>
    /// </summary>
    public int StepsAllowedFor(IEnumerable<string> registeredToolNames)
    {
        ArgumentNullException.ThrowIfNull(registeredToolNames);

        var budget = MaxSteps;
        foreach (var name in registeredToolNames)
        {
            foreach (var (prefix, steps) in ToolStepBudgets)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal) && steps > budget)
                {
                    budget = steps;
                }
            }
        }

        return budget;
    }

    public int MaxTokens { get; init; } = 4096;

    /// <summary>Upper bound on concurrently dispatched tool calls (design doc §2.9.5: bounded rolling pool).</summary>
    public int MaxParallelToolCalls { get; init; } = 4;

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long one approval question stays open before it is denied. Mirrors
    /// <see cref="Tools.QueuedApprovalHandler"/>'s own default so a caller that overrides one does not
    /// silently get the other's answer.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The ceiling on one step's whole tool dispatch — every call, in parallel, including the wait for a human.
    /// <para>
    /// Not redundant with <see cref="ToolTimeout"/>. Results are gathered with <c>Task.WhenAll</c>, which
    /// returns when the <i>slowest</i> call returns, and a per-call budget only binds a call that honours its
    /// own token. One tool that awaits something unbounded therefore withholds every result, and with it the
    /// step, the turn, and the single-reader consumer behind that — which is how a queue ends up reporting
    /// "running" with nothing running. This is the bound that does not assume the tools are well behaved.
    /// </para>
    /// </summary>
    public TimeSpan ToolDispatchTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// The model's context window in tokens; the prompt budget is this minus <see cref="MaxTokens"/>.</summary>
    public int ContextWindowTokens { get; init; } = 32768;

    /// <summary>
    /// A ceiling on one turn, enforced by the consumer loop rather than from in here.
    /// <para>
    /// Eight steps of a model call plus a tool call is minutes of legitimate work, so this is set well above
    /// what a real turn costs and exists only to catch the wedge: no inner budget is a guarantee, and the
    /// queue is single-reader, so a turn that never returns parks every message behind it with no way out but
    /// killing the process. Exposed for <see cref="Hosting.TurnLoop"/> to apply.
    /// </para>
    /// </summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public string? BaseSystemPrompt { get; init; }

    /// <summary>
    /// The system prompt, read at the start of every turn. When set it wins over <see cref="BaseSystemPrompt"/>:
    /// an edit to the prompt then reaches the next turn without restarting the consumer (a restart resets the
    /// turn counters and stops the MCP child processes, which is far too much for a wording change).
    /// </summary>
    public Func<string?>? PromptSource { get; init; }

    /// <summary>
    /// Which session this runner's turns belong to, as hooks see it. The main agent is <c>main</c>; a sub-agent
    /// job names its own so a hook can tell the two apart.
    /// </summary>
    public string SessionId { get; init; } = "main";
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
    private readonly IToolFailureLog? _failures;
    private readonly TimeProvider _clock;

    public AgentTurnRunner(
        ISessionStore session,
        ILlmClient llm,
        IToolRegistry tools,
        IToolGovernance governance,
        IApprovalHandler approval,
        IHookRegistry hooks,
        IContextAssembler? assembler = null,
        AgentTurnOptions? options = null,
        IAssistantStream? stream = null,
        IToolFailureLog? failures = null,
        TimeProvider? clock = null)
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
            session,
            _options.ContextWindowTokens,
            _options.MaxTokens,
            baseSystemPrompt: _options.BaseSystemPrompt,
            promptSource: _options.PromptSource);

        // Optional: a caller that wants the summary supplies a log, and one that does not pays nothing for it.
        // The clock is here for the same reason — "last seen" has to be a real instant, not a guess.
        _failures = failures;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The consumer loop's ceiling on one turn, applied there because a turn cannot be trusted to return.
    /// </summary>
    public TimeSpan TurnTimeout => _options.TurnTimeout;

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

        // Whether this turn's own message is already in the log, **read from the log** rather than from a
        // local flag.
        //
        // The input queue re-runs an interrupted `Running` item on restart (that is what at-least-once means
        // here), and a re-run is not a resume: nothing records which tool calls already happened, so the turn
        // starts over. With a local flag the re-run appended a *second* `User` event — same payload, same
        // meta.itemId, new seq — and that broke two things at once:
        //
        //   - the conversation showed the reader's message twice;
        //   - memory's at-least-once guard, which asks "is there already a record distilled from this event
        //     seq?", stopped matching, because the seq had moved. The fact was then stored twice and recall
        //     returned it as two independent pieces of evidence.
        //
        // Deriving it means a re-run reuses the original seq, so the guard matches again and nothing is
        // appended twice. The cost is one projection scan, and the memory hook in the same turn already does
        // exactly this scan.
        var projected = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var messageAlreadyLogged = false;
        foreach (var logged in projected.Items)
        {
            if (logged.Kind == SessionEventKind.User
                && logged.Meta is not null
                && logged.Meta.TryGetValue("itemId", out var loggedItemId)
                && string.Equals(loggedItemId, item.ItemId, StringComparison.Ordinal))
            {
                messageAlreadyLogged = true;
                break;
            }
        }

        var ctx = new HookContext
        {
            SessionId = _options.SessionId,
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

                // Empty means the message did not choose one, which leaves the configured default in place.
                var gear = string.IsNullOrWhiteSpace(item.ReasoningEffort) ? null : item.ReasoningEffort;

                // Seeded from the log, not from zero: this is the line that makes a retried turn reuse the
                // message it already logged instead of appending a second copy of it.
                var userLoggedHere = messageAlreadyLogged;
                var systemLogged = false;

                // The budget the toolset earns: a desktop turn gets room to look-act-look dozens of times,
                // a plain chat turn never notices the difference (it ends when the answer is written).
                var budget = _options.StepsAllowedFor(_tools.Specs.Select(spec => spec.Name));

                while (steps < budget)
                {
                    ct.ThrowIfCancellationRequested();

                    steps++;
                    await _session.AppendAsync(Event(SessionEventKind.StepStart, item.Source, $"step {steps}", item), ct)
                        .ConfigureAwait(false);

                    if (!userLoggedHere)
                    {
                        await _session.AppendAsync(Event(SessionEventKind.User, item.Source, item.Payload, item), ct)
                            .ConfigureAwait(false);
                        userLoggedHere = true;
                    }

                    if (!systemLogged && ctx.InjectedPrompts.Count > 0)
                    {
                        await _session.AppendAsync(
                            Event(SessionEventKind.System, item.Source, string.Join(" | ", ctx.InjectedPrompts), item), ct)
                            .ConfigureAwait(false);
                        systemLogged = true;
                    }

                    // Every step of this turn uses the gear the message carried, so a tool loop cannot
                    // silently drop back to the global default halfway through.
                    var request = new LlmRequest(
                        conversation,
                        _tools.Specs,
                        _options.MaxTokens,
                        ReasoningEffort: gear);
                    var streamed = _stream is null
                        ? null
                        : new Action<string>(delta => _stream.OnAssistantDelta(item.ItemId, delta));
                    var reasoningDelta = _stream is null
                        ? null
                        : new Action<string>(delta => _stream.OnReasoningDelta(item.ItemId, delta));

                    var response = streamed is null
                        ? await _llm.CompleteAsync(request, ct).ConfigureAwait(false)
                        : await _llm.StreamWithReasoningAsync(request, streamed, reasoningDelta!, ct).ConfigureAwait(false);

                    // Reasoning is recorded, not just shown live. A thinking row that only existed while the
                    // model was writing it would vanish the moment it answered — and "why did it do that" is
                    // exactly the question a reader has afterwards.
                    if (!string.IsNullOrWhiteSpace(response.Reasoning))
                    {
                        await _session.AppendAsync(
                            Event(SessionEventKind.Thinking, item.Source, response.Reasoning!, item), ct)
                            .ConfigureAwait(false);
                    }

                    _stream?.OnReasoningCompleted(item.ItemId);

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

                            // Counted here, not in the tool: `Ok == false` is the runtime saying the call did
                            // not happen the way it was asked to. A command that ran and exited non-zero is
                            // a *result* — that is the answer to a question, not a broken tool — so only
                            // what reaches this as a failure is a failure.
                            if (!result.Ok)
                            {
                                // The item id rides along so a summary row can be tied to the durable record of the
                                // same moment — the ToolResult event in the session log, whose `at` is within
                                // milliseconds of this. The queue log is not that record: a tool failing does
                                // not finish the turn, so the item stays Running and has no finishedAt at all.
                                _failures?.Record(
                                    call.Name,
                                    ToolFailureReason.From(result.Content),
                                    result.Content,
                                    _clock.GetUtcNow(),
                                    item.ItemId);
                            }

                            conversation.Add(new LlmMessage("tool", result.Content, ToolCallId: call.Id, Name: call.Name));

                            // Pictures a tool returned ride in a user message: the tool role carries text only, and
                            // the model has to see the image as an image. The bytes are not in the log — the result
                            // body names the media id, and the request carries the data URI.
                            if (result.Images is { Count: > 0 })
                            {
                                conversation.Add(new LlmMessage(
                                    "user",
                                    $"以下是工具 '{call.Name}' 返回的图片（共 {result.Images.Count} 张）。",
                                    Images: result.Images));
                            }
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

                if (steps >= budget)
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
                        Event(SessionEventKind.System, item.Source, $"error: {ex.Message}", item, ErrorMetaOf(ex)),
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

    /// <summary>
    /// The parts of a model failure that do not fit on one line: what kind, and the id to quote.
    ///
    /// <para>
    /// Kept in the event's meta rather than folded into the body, because the body is what a reader scans
    /// and these are what a reader looks up. Null for anything that is not a model failure — the keys must not
    /// exist when there is nothing behind them.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ErrorMetaOf(Exception error)
    {
        if (error is not ModelCallFailure failure)
        {
            return null;
        }

        var meta = new Dictionary<string, string>
        {
            ["error.kind"] = failure.Kind.ToString().ToLowerInvariant(),
            ["error.what"] = failure.What,
            ["error.reason"] = failure.Reason,
            ["error.next"] = failure.NextStep,
        };

        if (failure.RequestId is not null)
        {
            // The id is what the provider's support asks for first; without it a report is unactionable.
            meta["error.requestId"] = failure.RequestId;
        }

        return meta;
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
        // One budget for the dispatch rather than one per call: see ToolDispatchTimeout. It also covers the
        // approval wait, so a step cannot sit on a question that nobody is going to answer.
        using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dispatch.CancelAfter(_options.ToolDispatchTimeout);
        var token = dispatch.Token;

        var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelToolCalls));
        var tasks = new Task<ToolResult>[calls.Count];

        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            tasks[i] = Task.Run(async () =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return await ExecuteOneAsync(call, token).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None);
        }

        try
        {
            // Bounded <i>wait</i>, not just a cancelled token. Cancelling asks a tool to stop; it does not
            // make a tool that is parked on something unbounded come back, and `Task.WhenAll` returns only
            // when the slowest call does. So the aggregate is waited on alongside the clock: whichever arrives
            // first decides. Without this, one tool with an unbounded await withholds every result in the
            // step, and with it the turn and the queue behind the turn — which is the hang, exactly.
            var all = Task.WhenAll(tasks);
            var finished = await Task
                .WhenAny(all, Task.Delay(_options.ToolDispatchTimeout, CancellationToken.None))
                .ConfigureAwait(false);

            if (finished != all)
            {
                foreach (var task in tasks)
                {
                    Forget(task);
                }

                // Whatever finished in time is still a real answer and is kept: reporting a timeout for a
                // call that demonstrably happened would be its own kind of lie to the model.
                return Settled(tasks, calls, $"timeout after {_options.ToolDispatchTimeout.TotalSeconds:0}s: ");
            }

            await all.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation never fabricates success (design doc §2.9.5).
            return calls.Select(c => new ToolResult(false, $"canceled before completion: {c.Name}")).ToList();
        }
        finally
        {
            gate.Dispose();
        }

        return Settled(tasks, calls, "no result for ");
    }

    /// <summary>
    /// Keeps a tool call that will not finish from resurfacing as an unobserved task exception. Deliberately
    /// not awaited: the dispatch has moved on, and a fault on a task nobody is waiting for would otherwise be
    /// reported to nobody while still being reported somewhere.
    /// </summary>
    private static void Forget(Task abandoned) =>
        abandoned.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Reads results in <b>model order</b> — completion order is irrelevant to the conversation.</summary>
    private static IReadOnlyList<ToolResult> Settled(Task<ToolResult>[] tasks, IReadOnlyList<LlmToolCall> calls, string missing)
    {
        var results = new List<ToolResult>(calls.Count);
        for (var i = 0; i < tasks.Length; i++)
        {
            results.Add(tasks[i].IsCompletedSuccessfully
                ? tasks[i].Result
                : new ToolResult(false, missing + calls[i].Name));
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
                // Bounded here as well as in the handler: the handler's own clock is a default, and a caller
                // that injected a handler without one must not be able to park the dispatch indefinitely.
                bool approved;
                using (var question = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    question.CancelAfter(_options.ApprovalTimeout);
                    approved = await _approval.RequestAsync(call.Name, call.ArgumentsJson, question.Token)
                        .ConfigureAwait(false);
                }

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
