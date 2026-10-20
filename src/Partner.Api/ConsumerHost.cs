using Partner.Api.Agents;
using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Skills;
using Partner.Core.Agent.Mcp;
using Partner.Core.Agent.Tools;
using Partner.Core.Agent.Web;
using Partner.Desktop.Tools;
using Partner.Core.Hosting;
using Partner.Core.Memory;
using Partner.Core.Sessions;
using Partner.Core.Agent.Delegation;

namespace Partner.Api;

/// <summary>What the consumer is doing, so "nothing is executing" is a visible state rather than a silent one.</summary>
/// <summary>One tool the model can call, with the decision governance will apply to it.</summary>
public sealed record ToolExposure(string Name, string Decision);

/// <summary>The tools a sub-agent's job gets for one scenario, with the decision governance will apply to each.</summary>
public sealed record AgentToolExposure(string Scenario, string Agent, IReadOnlyList<ToolExposure> Tools);

public sealed record ConsumerState(
    bool Running,
    string Reason,
    string? Model,
    TurnLoopStats Stats,
    IReadOnlyList<ToolExposure> Tools,
    IReadOnlyList<AgentToolExposure> AgentTools);

/// <summary>
/// Owns the single consumer of the input queue (design doc §2.2) for the API process.
/// <para>
/// It starts only when a model is available. A consumer wired to a stub model would look like a working
/// runtime while executing nothing, which is worse than not starting: the reason travels in
/// <see cref="ConsumerState.Reason"/> and is served on <c>/api/consumer</c>.
/// </para>
/// <para>
/// Configuration can change at runtime, so the loop is replaceable: <see cref="ReconfigureAsync"/> stops the
/// current one and starts another. A settings edit that changed the model without restarting the loop would
/// leave the UI describing a configuration that is not in effect.
/// </para>
/// </summary>
public sealed class ConsumerHost : IAsyncDisposable, IAssistantStream
{
    private readonly ApiContext _context;
    private readonly string _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TurnLoop? _loop;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private HttpClient? _http;
    private ILlmClient? _llm;
    private IReadOnlyList<string> _toolNames = Array.Empty<string>();
    private DesktopGovernance? _governance;
    private string _reason = "未启动";

    /// <summary>The main model's window, kept so a sub-agent that follows the main model gets the same numbers.</summary>
    private int _mainContextTokens = 32768;
    private int _mainMaxTokens = 4096;

    /// <summary>
    /// The MCP servers this consumer started, kept so a reconfigure can stop them.
    /// <para>
    /// Held on the host rather than dropped after registration: a client that is never stopped leaves a child
    /// process behind on every settings save, and those accumulate.
    /// </para>
    /// </summary>
    private readonly List<IMcpClient> _mcpClients = new();

    /// <summary>Why MCP is not running, in the terms a person can act on. Null when nothing is configured.</summary>
    public string? McpNote { get; private set; }

    /// <summary>Used only to timestamp "thinking since"; a display value, not anything the runtime decides on.</summary>
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>
    /// What the tools have been failing at, counted. Lives on the host rather than in a turn so it survives a
    /// restart of the consumer — the failures are the point, and losing them on every model change is what
    /// makes a recurring problem invisible.
    /// </summary>
    private readonly IToolFailureLog _failures = new InMemoryToolFailureLog();

    /// <summary>Tool failures, most frequent first. Bounded by the log's own capacity.</summary>
    public ToolFailureSummary Failures(int take) => new(
        _failures.Top(take).Select(f => new ToolFailureView(
            f.Tool, f.Reason, f.Count, f.LastSeen.ToString("O"), f.LastMessage, f.ItemId)).ToList(),
        new ToolFailureTotalsView(_failures.Totals.Distinct, _failures.Totals.Occurrences));

    /// <summary>
    /// The assistant text of the turn in flight, or null when nothing is being written. Deliberately not the
    /// session log: a half-written reply is not an event of record, so it lives here and disappears the moment
    /// the real <c>Assistant</c> event lands.
    /// </summary>
    private readonly object _liveGate = new();
    private LiveAssistant? _live;
    private string? _model;

    private ConsumerHost(ApiContext context, string workspace)
    {
        _context = context;
        _workspace = workspace;
        _agents = new SubAgentHost(context, workspace, ResolveModel, _failures, _clock);
    }

    private readonly SubAgentHost _agents;

    /// <summary>The sub-agents' jobs. Owned here because a job's life is tied to the consumer's: rebuilding one cancels the other's work.</summary>
    public SubAgentHost Agents => _agents;

    /// <summary>
    /// The model a sub-agent's job should run on: its own when it has one, the main model otherwise. Null when
    /// there is no main model to follow — a job started then would have nothing to think with.
    /// </summary>
    private ModelBinding? ResolveModel(SubAgentSettings agent)
    {
        if (agent.Model is { } own)
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new HttpLlmClient(http, new HttpLlmOptions
            {
                BaseUrl = own.BaseUrl!.TrimEnd('/'),
                Model = own.Model!,
                ApiKey = own.ApiKey,
                Timeout = TimeSpan.FromSeconds(own.TimeoutSeconds),
                ReasoningEffort = own.ReasoningEffort,
            });

            return new ModelBinding(
                client,
                new DualArmTokenCounter(client, new ApproximateTokenCounter()),
                own.ContextTokens,
                own.MaxTokens,
                own.SupportsImages,
                http);
        }

        var llm = _llm;
        return llm is null || _running is not { IsCompleted: false }
            ? null
            : new ModelBinding(llm, Tokens, _mainContextTokens, _mainMaxTokens, _context.Settings.Load().Model.SupportsImages);
    }

    public ConsumerState State => new(
        Running: _running is { IsCompleted: false },
        Reason: _reason,
        Model: _model,
        Stats: _loop?.Stats ?? TurnLoopStats.Empty,

        // Decided when it is read, not when the loop started: the approval gear is live, so a snapshot would
        // report a decision the runtime no longer makes.
        Tools: _toolNames
            .Select(name => new ToolExposure(
                name,
                (_governance?.Decide(name, "{}").Decision ?? ToolDecision.Deny).ToString().ToLowerInvariant()))
            .ToList(),
        AgentTools: SubAgentTools());

    /// <summary>
    /// What each reachable sub-agent can call. The tools are not in the main agent's table any more, and
    /// "what can the model touch, and what will it ask first" is exactly the thing a person needs to be able to
    /// read off the status endpoint — so it is reported here, decided live like the main table.
    /// </summary>
    private IReadOnlyList<AgentToolExposure> SubAgentTools()
    {
        if (_governance is null)
        {
            return Array.Empty<AgentToolExposure>();
        }

        var settings = _context.Settings.Load();
        return AgentRouting.Effective(settings)
            .Select(route =>
            {
                // One branch per scenario, mirroring the tool set the job runner registers for it: reporting the
                // web-search agent's tools through the default branch would tell a person it can read files.
                var names = route.Scenario.Key switch
                {
                    AgentScenarios.Desktop => DesktopToolCatalog.All
                        .Where(name => name != "desktop_screenshot" || (route.Agent.Model?.SupportsImages ?? settings.Model.SupportsImages))
                        .ToList(),
                    AgentScenarios.WebSearch => new List<string> { WebSearchTool.Name, WebFetchTool.Name },
                    _ => new List<string> { "file_list", "file_read" },
                };

                return new AgentToolExposure(
                    route.Scenario.Key,
                    route.Agent.Name,
                    names.Select(name => new ToolExposure(
                        name,
                        _governance.Decide(name, "{}").Decision.ToString().ToLowerInvariant())).ToList());
            })
            .ToList();
    }

    public static ConsumerHost Create(ApiContext context, string workspace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        return new ConsumerHost(context, workspace);
    }

    /// <summary>
    /// The counter in use: the model's own tokenizer when one is configured, the local estimate otherwise.
    /// </summary>
    public ITokenCounter Tokens { get; private set; } = new ApproximateTokenCounter();

    /// <summary>What the model is writing right now, for the session view to show as it grows.</summary>
    public LiveAssistant? LiveAssistant
    {
        get
        {
            lock (_liveGate)
            {
                return _live;
            }
        }
    }

    void IAssistantStream.OnAssistantDelta(string itemId, string delta)
    {
        lock (_liveGate)
        {
            // A new item starts a new live text; the old one belongs to a step that already ended. The same is
            // true of a new step within a turn, which is why the item id is the identity here.
            _live = _live is { } current && current.ItemId == itemId
                ? current with
                {
                    Text = current.Text + delta,

                    // The first word of an answer ends the thinking: leaving "thinking" running while the
                    // answer streams would tell the reader it is still working when it is not.
                    Reasoning = null,
                    ReasoningSince = null,
                }
                : new LiveAssistant(itemId, delta);
        }
    }

    void IAssistantStream.OnAssistantCompleted(string itemId)
    {
        lock (_liveGate)
        {
            if (_live is { } current && current.ItemId == itemId)
            {
                _live = null;
            }
        }
    }

    void IAssistantStream.OnReasoningDelta(string itemId, string delta)
    {
        lock (_liveGate)
        {
            var current = _live is { } live && live.ItemId == itemId ? live : null;

            _live = current is null
                ? new LiveAssistant(itemId, string.Empty, delta, _clock.GetUtcNow())
                : current with
                {
                    Reasoning = (current.Reasoning ?? string.Empty) + delta,
                    ReasoningSince = current.ReasoningSince ?? _clock.GetUtcNow(),
                };
        }
    }

    void IAssistantStream.OnReasoningCompleted(string itemId)
    {
        lock (_liveGate)
        {
            // The text stays: the log now holds the finished thinking, but a view that is mid-poll should
            // still see the last of it rather than the row blinking empty before the answer arrives.
            if (_live is { } current && current.ItemId == itemId && current.Reasoning is not null)
            {
                _live = current with { ReasoningSince = null };
            }
        }
    }

    /// <summary>How much of the context window the session currently occupies.</summary>
    public async Task<ContextUsage> MeasureContextAsync(RuntimeSettings settings, CancellationToken ct = default)
    {
        var assembler = new SessionWindowContextAssembler(
            _context.Session,
            settings.Model.ContextTokens,
            settings.Model.MaxTokens,
            Tokens);

        return await assembler.MeasureAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds history that no longer fits the window. Only possible while a model is configured, because the
    /// summary is produced by that model.
    /// </summary>
    public Task<CompactionResult> CompactAsync(int promptBudget, CancellationToken ct = default)
    {
        var llm = _llm;

        return llm is null
            ? Task.FromResult(new CompactionResult(0, null, "未配置模型，无法压缩"))
            : new SessionCompactor(_context.Session, llm, Tokens).CompactAsync(promptBudget, ct);
    }

    /// <summary>
    /// Applies a configuration: stops whatever is running, then starts the loop if one is usable.
    /// <paramref name="injected"/> is used verbatim when present — it is how tests and diagnostics supply a
    /// scripted model, and it has no endpoint to build a client from.
    /// </summary>
    public async Task<ConsumerState> ReconfigureAsync(ModelConfig? config, string reason, ILlmClient? injected = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Before the tool table is rebuilt: a job holds the one it was started with.
            await _agents.CancelAllAsync().ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);

            if (config is null && injected is null)
            {
                _reason = reason;
                _model = null;
                _toolNames = Array.Empty<string>();
                _governance = null;
                return State;
            }

            Start(config, reason, injected);
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Start(ModelConfig? config, string reason, ILlmClient? injected)
    {
        ILlmClient llm;

        if (injected is not null)
        {
            llm = injected;
            // An injected client has no tokenizer endpoint to ask.
            Tokens = new ApproximateTokenCounter();
        }
        else
        {
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new HttpLlmClient(_http, new HttpLlmOptions
            {
                BaseUrl = config!.BaseUrl,
                Model = config.Model,
                ApiKey = config.ApiKey,
                Timeout = config.Timeout,
                ReasoningEffort = config.ReasoningEffort,
            });

            llm = client;

            // Two arms: the model's own tokenizer where the server has one, the local estimate as the floor.
            Tokens = new DualArmTokenCounter(client, new ApproximateTokenCounter());
        }

        var turnOptions = new AgentTurnOptions
        {
            ContextWindowTokens = config?.ContextTokens ?? 32768,
            MaxTokens = config?.MaxTokens ?? 4096,

            // Read at the start of every turn, so editing the main prompt does not restart anything.
            PromptSource = () => AgentRouting.MainPrompt(_context.Settings.Load()),
        };
        _mainContextTokens = turnOptions.ContextWindowTokens;
        _mainMaxTokens = turnOptions.MaxTokens;

        // Built here rather than left to the runner's default: the assembler is the one that can reach the
        // media store, so it is the one that attaches the input's images.
        var assembler = new SessionWindowContextAssembler(
            _context.Session,
            turnOptions.ContextWindowTokens,
            turnOptions.MaxTokens,
            Tokens,
            turnOptions.BaseSystemPrompt,
            _context.Media,
            turnOptions.PromptSource);

        var settings = _context.Settings.Load();
        var tools = new InMemoryToolRegistry();

        // Desktop tools are no longer registered here, and that is the point of the sub-agents: the main agent
        // hands desktop work to the desktop sub-agent and never holds the tools itself. File tools stay with the
        // main agent only while no enabled sub-agent has taken them ("no scenario configured" = main does it).
        if (AgentRouting.MainOwnsFiles(settings))
        {
            FileToolSet.Register(tools, settings, _workspace);
        }

        // On by default: web search is the capability this build ships enabled, so out of the box these two
        // tools are the main agent's. They move to a web-search sub-agent the moment one is created and enabled.
        if (AgentRouting.MainOwnsWeb(settings))
        {
            WebToolSet.Register(tools, _context);
        }

        RegisterMcpTools(tools, settings.Mcp);

        // Always the main agent's, and not behind a switch: timed tasks are part of the product, not a capability
        // that reaches outside it. What guards them is governance (creating and deleting ask a human).
        tools.Register(new ScheduleCreateTool(_context));
        tools.Register(new ScheduleListTool(_context));
        tools.Register(new ScheduleDeleteTool(_context));

        var routes = AgentRouting.Effective(settings);
        if (routes.Count > 0)
        {
            tools.Register(new AgentDelegateTool(_agents, AgentRouting.DelegationScenarios(settings)));
            tools.Register(new AgentJobStatusTool(_agents));
            tools.Register(new AgentJobCancelTool(_agents));
        }

        _toolNames = tools.Specs.Select(spec => spec.Name).ToList();

        var governance = new DesktopGovernance(_context);
        _governance = governance;

        var runner = new AgentTurnRunner(
            _context.Session,
            llm,
            tools,
            // Fail-closed and live: the desktop tools answer to the current approval gear, and anything that is
            // not a desktop tool is denied because nothing else is registered.
            governance,
            // Approval, intercepted: a granted desktop action also opens its continuing grant on the same
            // window, which is what turns "approve each of 24 clicks" into "approve the window". The wrap
            // forwards everything else unchanged.
            new LeaseGrantingApprovalHandler(_context.Approvals, _context.TaskLeases),
            TurnHooks(_workspace),
            assembler: assembler,
            options: turnOptions,
            stream: this,
            failures: _failures,
            clock: _clock);

        _loop = new TurnLoop(_context.Queue, runner, _context.Session);
        _stop = new CancellationTokenSource();
        _running = _loop.RunAsync(_stop.Token);
        _llm = llm;
        _model = injected is not null ? injected.GetType().Name : config!.Model;
        _reason = reason;
    }

    /// <summary>
    /// The hooks a turn runs: what it already knows, and what it leaves behind.
    /// <para>
    /// Memory is always registered, unlike skills. Skills need a workspace to load from, but the memory store
    /// always exists — and registering it only when non-empty is how it stayed empty for so long: there was no
    /// first record for such a condition to ever notice.
    /// </para>
    /// </summary>
    private HookRegistry TurnHooks(string workspace)
    {
        var hooks = new HookRegistry();

        hooks.Register(new MemoryRecallHook(_context.Memory, _context.Session));
        hooks.Register(new MemoryDistilHook(_context.Memory, _context.Session, _clock));

        // A link the person pasted is a link they meant to open, so it goes into the ledger that web_fetch's
        // governance reads: fetching it then needs no approval banner. Registered unconditionally — the hook
        // only remembers hosts, and the ledger is the same object whether or not the tools exist this run.
        hooks.Register(new Partner.Core.Agent.Web.WebHostLedgerHook(_context.Web.Ledger));

        var disabled = _context.Settings.Load().Skills.Disabled.ToHashSet(StringComparer.Ordinal);
        var loaded = new SkillLoader().Load(workspace);
        if (loaded.Skills.Count > 0)
        {
            var registry = new InMemorySkillRegistry();

            // A skill the person switched off is not loaded at all, rather than loaded and filtered at use:
            // the point of switching one off is that it stops being part of the prompt, not that it becomes
            // something to reason about excluding.
            registry.Reload(loaded.Skills.Where(skill => !disabled.Contains(skill.SkillId)).ToList());
            hooks.Register(new SkillInjectionHook(registry));
        }

        return hooks;
    }

    /// <summary>
    /// Starts the configured MCP servers and offers their tools to the model.
    /// <para>
    /// Best-effort per server, and deliberately: one server that will not start must not cost the model the
    /// tools of the others, nor take the whole consumer down. What went wrong is recorded in
    /// <see cref="McpNote"/> rather than thrown — a settings save should not fail over a server that happens to
    /// be down, and "the tool you wanted is missing" is a far worse thing to debug than a line naming the
    /// server that refused.
    /// </para>
    /// </summary>
    private void RegisterMcpTools(InMemoryToolRegistry tools, McpSettings settings)
    {
        McpNote = null;

        if (!settings.Enabled || settings.Servers.Count == 0)
        {
            return;
        }

        var failures = new List<string>();

        foreach (var server in settings.Servers)
        {
            IMcpClient client;
            try
            {
                client = new StdioMcpClient(new McpServerConfig
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Arguments,
                    WorkingDirectory = server.WorkingDirectory,
                    CallTimeout = TimeSpan.FromSeconds(Math.Max(1, server.CallTimeoutSeconds)),
                });

                client.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failures.Add($"{server.Name}: {error.Message}");
                continue;
            }

            _mcpClients.Add(client);

            try
            {
                foreach (var descriptor in client.ListToolsAsync().GetAwaiter().GetResult())
                {
                    // Namespaced so two servers can offer the same tool name without one shadowing the other,
                    // and so a reader can tell where a tool came from.
                    tools.Register(new NamespacedMcpTool(client, server.Name, descriptor));
                }
            }
            catch (Exception error)
            {
                failures.Add($"{server.Name}: tools/list 失败（{error.Message}）");
            }
        }

        if (failures.Count > 0)
        {
            McpNote = string.Join("；", failures);
        }
    }

    /// <summary>Stops the servers this consumer started, so a reconfigure leaves no child processes behind.</summary>
    private async Task StopMcpAsync()
    {
        foreach (var client in _mcpClients)
        {
            try
            {
                await client.StopAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"mcp server did not stop cleanly: {error.Message}");
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }

        _mcpClients.Clear();
    }

    /// <summary>
    /// Stops claiming new work and waits for the turn in flight. An interrupted item is re-queued by the queue
    /// on the next start, so this is a graceful stop rather than a kill.
    /// </summary>
    private async Task StopAsync()
    {
        if (_stop is not null && _running is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await _running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop was waiting for work.
            }

            _stop.Dispose();
            _http?.Dispose();

            _stop = null;
            _running = null;
            _loop = null;
            _http = null;
        }

        // Outside the guard on purpose: a consumer that never started still may have started servers, and a
        // child process left behind is invisible until someone notices the machine is busy.
        await StopMcpAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _agents.DisposeAsync().ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
