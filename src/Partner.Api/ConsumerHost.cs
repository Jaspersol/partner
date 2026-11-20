using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Api.Agents;
using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Skills;
using Partner.Core.Agent.Mcp;
using Partner.Core.Agent.Tools;
using Partner.Core.Hosting;
using Partner.Core.Memory;
using Partner.Core.Sessions;
using Partner.Core.Agent.Delegation;

namespace Partner.Api;

/// <summary>One exposed tool and its baseline governance decision; arguments and leases may change a call.</summary>
public sealed record ToolExposure(string Name, string Decision);

/// <summary>The tools a sub-agent's job gets for one scenario, with baseline governance decisions.</summary>
public sealed record AgentToolExposure(string Scenario, string Agent, IReadOnlyList<ToolExposure> Tools);

public sealed record ConsumerState(
    bool Running,
    string Reason,
    string? Model,
    TurnLoopStats Stats,
    IReadOnlyList<ToolExposure> Tools,
    IReadOnlyList<AgentToolExposure> AgentTools);

/// <summary>
/// Owns the single consumer of the input queue for the API process.
/// <para>
/// It starts only when a model is available. A consumer wired to a stub model would look like a working
/// runtime while executing nothing: the reason travels in <see cref="ConsumerState.Reason"/> and is served on
/// <c>/api/consumer</c>.
/// </para>
/// <para>
/// Endpoint changes replace the loop; capability and scenario changes instead publish a new tool snapshot
/// for the next turn while current calls remain subject to live governance.
/// </para>
/// </summary>
public sealed class ConsumerHost : IAsyncDisposable, IAssistantStream
{
    private readonly ApiContext _context;
    private readonly string _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _startupGate = new();
    private CancellationTokenSource? _startup;

    private TurnLoop? _loop;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private HttpClient? _http;
    private ILlmClient? _llm;
    private IReadOnlyList<string> _toolNames = Array.Empty<string>();
    private DesktopGovernance? _governance;
    private sealed record TurnSnapshot(AgentTurnRunner Runner, SessionWindowContextAssembler Assembler, IReadOnlyList<string> Names, Func<int> SchemaTokens, RuntimeSettings Settings);
    private readonly object _snapshotGate = new();
    private TurnSnapshot? _turnSnapshot;
    private Func<RuntimeSettings, TurnSnapshot>? _buildTurnSnapshot;
    private string _reason = "未启动";

    /// <summary>The main model's window, kept so a sub-agent that follows the main model gets the same numbers.</summary>
    private int _mainContextTokens = 128000;
    private int _mainMaxTokens = 32000;

    // Connections and descriptors outlive a consumer loop rebuild, but not a server configuration change.
    private readonly List<IMcpClient> _mcpClients = new();
    private McpSettings? _connectedMcpSettings;
    private IReadOnlyList<McpAttempt> _mcpAttempts = Array.Empty<McpAttempt>();
    internal Func<McpServerConfig, IMcpClient> McpClientFactory { get; set; } = config => new StdioMcpClient(config);

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

    /// <summary>
    /// The skills the hook injects. One registry for the life of the host, reloaded in place, so adding or removing
    /// a skill reaches the next turn without rebuilding the consumer (which would drop the model's warm state).
    /// </summary>
    private readonly InMemorySkillRegistry _skillRegistry = new();

    private ConsumerHost(ApiContext context, string workspace)
    {
        _context = context;
        _workspace = workspace;
        _agents = new SubAgentHost(context, workspace, ResolveModel, _failures, _clock);
    }

    private readonly SubAgentHost _agents;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> _subClients = new(StringComparer.Ordinal);

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
            // One client per endpoint for the life of the consumer, not one per job: a per-job client would open a
            // connection (and TLS handshake) for a model it had just been talking to.
            var http = _subClients.GetOrAdd(
                $"{LlmProtocols.Normalize(own.Protocol)}|{own.BaseUrl!.TrimEnd('/')}",
                _ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            var client = LlmClientFactory.Create(http, new HttpLlmOptions
            {
                BaseUrl = own.BaseUrl!.TrimEnd('/'),
                Model = own.Model!,
                ApiKey = own.ApiKey,
                Timeout = TimeSpan.FromSeconds(own.TimeoutSeconds),
                ReasoningEffort = own.ReasoningEffort,
                Protocol = own.Protocol,
                MaxOutputTokens = own.MaxTokens,
            });

            return new ModelBinding(
                client.Client,
                new DualArmTokenCounter(client.Tokens, new ApproximateTokenCounter()),
                own.ContextTokens,
                own.MaxTokens,
                own.SupportsImages);
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
                ToolStatus(name)))
            .ToList(),
        AgentTools: SubAgentTools());

    /// <summary>
    /// What each reachable sub-agent can call. The tools are not in the main agent's table, and "what can the
    /// model touch, and what will it ask first" must be readable from the status endpoint, decided live like the
    /// main table.
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
                var names = AgentRouting.ScenarioTools(
                    route.Scenario.Key, settings,
                    route.Agent.Model?.SupportsImages ?? settings.Model.SupportsImages);

                return new AgentToolExposure(
                    route.Scenario.Key,
                    route.Agent.Name,
                    names.Select(name => new ToolExposure(
                        name,
                        ToolStatus(name))).ToList());
            })
            .ToList();
    }

    private string ToolStatus(string name)
    {
        if (_governance is null) return "deny";
        var decision = _governance.Summarize(name).Decision;
        if (decision != ToolDecision.Deny)
        {
            var settings = _context.Settings.Load();
            if (name == "desktop_act" && ApprovalGears.Normalize(settings.Desktop.Approval) != ApprovalGears.None)
                return "conditional";
            if (name == "web_fetch" && ApprovalGears.Normalize(settings.Desktop.Approval) == ApprovalGears.All)
                return "conditional";
        }
        return decision.ToString().ToLowerInvariant();
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

    private Func<int> _toolSchemaTokens = static () => 0;

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
            // A new item starts a new live text; the old one belongs to a step that already ended. The same holds
            // for a new step within a turn, which is why the item id is the identity here.
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
            // The text stays: the log holds the finished thinking, but a view that is mid-poll should still see
            // the last of it rather than the row blinking empty before the answer arrives.
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
            Tokens,
            reservedTokens: Volatile.Read(ref _toolSchemaTokens));

        return await assembler.MeasureAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A scheduled message has been answered (or has failed): write that on the task, so the page can show what
    /// its last run did instead of only that the message was delivered.
    /// </summary>
    private async Task RecordScheduledRunAsync(InputItem item, InputItemStatus status, string? error)
    {
        if (item.Source != InputSource.Schedule || status == InputItemStatus.Canceled)
        {
            return;
        }

        // schedule:{taskId}:{occurrence}, written by the scheduler; the task id has no colon in it.
        var parts = item.IdempotencyKey.Split(':', 3);
        if (parts.Length < 2 || parts[0] != "schedule")
        {
            return;
        }

        await _context.Scheduler
            .RecordRunAsync(parts[1], status == InputItemStatus.Done, error, _clock.GetUtcNow())
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Past this share of the prompt budget (or as soon as the assembler had to drop history), a finished turn is
    /// followed by a compaction pass instead of letting the oldest entries fall out of the window silently.
    /// </summary>
    internal const double AutoCompactFraction = 0.8;

    private async Task CompactIfNeededAsync(SessionWindowContextAssembler assembler, CancellationToken ct)
    {
        var usage = await assembler.MeasureAsync(ct).ConfigureAwait(false);
        if (usage.Fraction > AutoCompactFraction || usage.FoldedItems > 0)
        {
            await CompactAsync(SessionCompactor.HistoryBudget(usage), ct, usage.BudgetTokens).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Folds history that no longer fits the window. Only possible while a model is configured, because the
    /// summary is produced by that model.
    /// </summary>
    public Task<CompactionResult> CompactAsync(int promptBudget, CancellationToken ct = default, int? summaryInputBudget = null)
    {
        var llm = _llm;

        return llm is null
            ? Task.FromResult(new CompactionResult(0, null, "未配置模型，无法压缩"))
            : new SessionCompactor(_context.Session, llm, Tokens).CompactAsync(promptBudget, ct, summaryInputBudget);
    }


    /// <summary>
    /// Applies a configuration: stops whatever is running, then starts the loop if one is usable.
    /// <paramref name="injected"/> is used verbatim when present — it is how tests and diagnostics supply a
    /// scripted model, and it has no endpoint to build a client from.
    /// </summary>
    public async Task<ConsumerState> ReconfigureAsync(ModelConfig? config, string reason, ILlmClient? injected = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        lock (_startupGate)
        {
            _startup?.Cancel();
            _startup = startup;
        }

        try
        {
            await _gate.WaitAsync(startup.Token).ConfigureAwait(false);
            try
            {
                // Before the tool table is rebuilt: a job holds the one it was started with.
                await _agents.CancelAllAsync().ConfigureAwait(false);
                await StopAsync().ConfigureAwait(false);

                if (config is null && injected is null)
                {
                    await StopMcpAsync().ConfigureAwait(false);
                    _reason = reason;
                    _model = null;
                    _toolNames = Array.Empty<string>();
                    _governance = null;
                    return State;
                }

                try
                {
                    await StartAsync(config, reason, injected, startup.Token).ConfigureAwait(false);
                }
                catch
                {
                    await StopAsync().ConfigureAwait(false);
                    await StopMcpAsync().ConfigureAwait(false);
                    _http?.Dispose();
                    _http = null;
                    _llm = null;
                    _model = null;
                    _toolNames = Array.Empty<string>();
                    _governance = null;
                    throw;
                }
                return State;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            lock (_startupGate)
            {
                if (ReferenceEquals(_startup, startup)) _startup = null;
            }
        }
    }

    private async Task StartAsync(ModelConfig? config, string reason, ILlmClient? injected, CancellationToken ct)
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
            var client = LlmClientFactory.Create(_http, new HttpLlmOptions
            {
                BaseUrl = config!.BaseUrl,
                Model = config.Model,
                ApiKey = config.ApiKey,
                Timeout = config.Timeout,
                ReasoningEffort = config.ReasoningEffort,
                PromptCacheMarkers = config.PromptCache,
                Protocol = config.Protocol,
                MaxOutputTokens = config.MaxTokens,
            });

            llm = client.Client;

            // Two arms: the model's own tokenizer where the server has one, the local estimate as the floor.
            Tokens = new DualArmTokenCounter(client.Tokens, new ApproximateTokenCounter());
        }

        var turnOptions = new AgentTurnOptions
        {
            ContextWindowTokens = config?.ContextTokens ?? 128000,
            MaxTokens = config?.MaxTokens ?? 32000,

            // Read at the start of every turn, so editing the main prompt does not restart anything.
            PromptSource = () => AgentRouting.MainPrompt(_context.Settings.Load()),
        };
        _mainContextTokens = turnOptions.ContextWindowTokens;
        _mainMaxTokens = turnOptions.MaxTokens;

        var settings = _context.Settings.Load();
        var mcpTools = new InMemoryToolRegistry();
        await RegisterMcpToolsAsync(mcpTools, settings.Mcp, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var tokens = Tokens;
        var governance = new DesktopGovernance(_context, mainAgent: true);
        _governance = new DesktopGovernance(_context);
        var hooks = TurnHooks(_workspace);

        TurnSnapshot BuildSnapshot(RuntimeSettings current)
        {
            var tools = new InMemoryToolRegistry();
            if (AgentRouting.MainOwnsFiles(current))
                AgentRouting.RegisterFamily(AgentScenarios.FileRead, tools, _context, current, current.Model.SupportsImages, _workspace);
            if (AgentRouting.MainOwnsWeb(current))
                AgentRouting.RegisterFamily(AgentScenarios.WebSearch, tools, _context, current, current.Model.SupportsImages, _workspace);
            foreach (var spec in mcpTools.Specs)
                tools.Register(mcpTools.Find(spec.Name)!);
            AgentRouting.RegisterMainTools(tools, _context, _agents, current);

            Func<int> schemaTokens = () => ToolResultBudget.SchemaTokens(tools.Specs, tokens);
            var assembler = new SessionWindowContextAssembler(
                _context.Session, turnOptions.ContextWindowTokens, turnOptions.MaxTokens, tokens,
                turnOptions.BaseSystemPrompt, _context.Media, turnOptions.PromptSource, reservedTokens: schemaTokens,
                memory: _context.Memory, scheduler: _context.Scheduler);
            var runner = new AgentTurnRunner(
                _context.Session, llm, tools, governance,
                new LeaseGrantingApprovalHandler(_context.Approvals, _context.TaskLeases),
                hooks, assembler: assembler, options: turnOptions, stream: this,
                failures: _failures, clock: _clock, tokens: tokens);
            return new TurnSnapshot(runner, assembler, tools.Specs.Select(spec => spec.Name).ToArray(), schemaTokens, current);
        }

        _buildTurnSnapshot = BuildSnapshot;
        PublishTurnSnapshot(BuildSnapshot(settings));

        _loop = new TurnLoop(
            _context.Queue,
            () => SnapshotForTurn().Runner,
            _context.Session,
            afterTurn: ct => CompactIfNeededAsync(Volatile.Read(ref _turnSnapshot)!.Assembler, ct),
            onSettled: RecordScheduledRunAsync);
        _stop = new CancellationTokenSource();
        _running = _loop.RunAsync(_stop.Token);
        _llm = llm;
        _model = injected is not null ? injected.GetType().Name : config!.Model;
        _reason = reason;
    }

    private void PublishTurnSnapshot(TurnSnapshot snapshot)
    {
        _toolNames = snapshot.Names;
        Volatile.Write(ref _toolSchemaTokens, snapshot.SchemaTokens);
        Volatile.Write(ref _turnSnapshot, snapshot);
    }

    private TurnSnapshot SnapshotForTurn()
    {
        lock (_snapshotGate)
        {
            var snapshot = _turnSnapshot!;
            var settings = _context.Settings.Load();
            if (AgentRouting.ToolTableDiffers(snapshot.Settings, settings)
                || snapshot.Settings.Model.SupportsImages != settings.Model.SupportsImages)
            {
                snapshot = _buildTurnSnapshot!(settings);
                PublishTurnSnapshot(snapshot);
            }
            return snapshot;
        }
    }

    /// <summary>Updates future turns without stopping the consumer, model or MCP processes.</summary>
    public async Task RefreshToolsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_snapshotGate)
            {
                if (_buildTurnSnapshot is { } build)
                {
                    var settings = _context.Settings.Load();
                    var current = _turnSnapshot;
                    if (current is null || AgentRouting.ToolTableDiffers(current.Settings, settings)
                        || current.Settings.Model.SupportsImages != settings.Model.SupportsImages)
                        PublishTurnSnapshot(build(settings));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The hooks a turn runs: what it already knows, and what it leaves behind.
    /// <para>
    /// Memory is always registered, unlike skills. Skills need a workspace to load from, but the memory store
    /// always exists, and registering it only when non-empty would keep it empty.
    /// </para>
    /// </summary>
    private HookRegistry TurnHooks(string workspace)
    {
        var hooks = new HookRegistry();

        hooks.Register(new MemoryDistilHook(_context.Memory, _context.Session, _clock));

        // A link the person pasted is a link they meant to open, so it goes into the ledger that web_fetch's
        // governance reads: fetching it then needs no approval banner. Registered unconditionally — the hook
        // only remembers hosts, and the ledger is the same object whether or not the tools exist this run.
        hooks.Register(new Partner.Core.Agent.Web.WebHostLedgerHook(_context.Web.Ledger));

        // Registered even when empty (an empty registry injects nothing): the registry is reloaded in place later.
        ReloadSkills();
        hooks.Register(new SkillInjectionHook(_skillRegistry));

        return hooks;
    }

    /// <summary>
    /// Re-reads the workspace's skills and the deny list into the live registry. Called when a skill is added or
    /// removed and when the deny list changes, so the next turn sees the new set.
    /// </summary>
    public void ReloadSkills()
    {
        var disabled = _context.Settings.Load().Skills.Disabled.ToHashSet(StringComparer.Ordinal);
        var loaded = new SkillLoader().Load(_workspace);

        // A skill the person switched off is not loaded at all, rather than loaded and filtered at use:
        // the point of switching one off is that it stops being part of the prompt, not that it becomes
        // something to reason about excluding.
        _skillRegistry.Reload(loaded.Skills.Where(skill => !disabled.Contains(skill.SkillId)).ToList());
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
    private async Task RegisterMcpToolsAsync(InMemoryToolRegistry tools, McpSettings settings, CancellationToken ct)
    {
        if (_connectedMcpSettings is null
            || _connectedMcpSettings.Enabled != settings.Enabled
            || !_connectedMcpSettings.Servers.SequenceEqual(settings.Servers))
        {
            await StopMcpAsync().ConfigureAwait(false);
            if (settings.Enabled && settings.Servers.Count > 0)
            {
                await ConnectMcpServersAsync(settings, ct).ConfigureAwait(false);
            }
            _connectedMcpSettings = settings;
        }

        var failures = new List<string>();
        foreach (var (server, attempt) in settings.Servers.Zip(_mcpAttempts))
        {
            if (attempt.Client is null)
                failures.Add($"{server.Name}: {attempt.StartError}");
            else if (attempt.ListError is not null)
                failures.Add($"{server.Name}: tools/list 失败（{attempt.ListError}）");
        }

        // Names use an underscore separator, so (server=a_b, tool=c) and
        // (server=a, tool=b_c) collide. Never let registration silently replace
        // one server's tool with another, especially when the name is allowlisted.
        var discovered = settings.Servers.Zip(_mcpAttempts)
            .Where(pair => pair.Second.Client is not null && pair.Second.ListError is null)
            .SelectMany(pair => pair.Second.Tools.Select(descriptor =>
                (Server: pair.First.Name, Client: pair.Second.Client!, Descriptor: descriptor,
                    Name: NamespacedMcpTool.Name(pair.First.Name, descriptor.Name))))
            .ToArray();
        var collisions = discovered.GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (collisions.Count > 0)
            failures.Add($"MCP 工具名称冲突，已拒绝：{string.Join("、", collisions.OrderBy(name => name, StringComparer.Ordinal))}");
        McpNote = failures.Count == 0 ? null : string.Join("；", failures);
        foreach (var item in discovered)
        {
            if (!collisions.Contains(item.Name))
                tools.Register(new NamespacedMcpTool(item.Client, item.Server, item.Descriptor));
        }
    }

    private async Task ConnectMcpServersAsync(McpSettings settings, CancellationToken ct)
    {
        // Connect in parallel, but preserve settings order for registration and diagnostics.
        var attempts = settings.Servers.Select(server => Task.Run(() => ConnectMcpAsync(server, ct))).ToArray();
        McpAttempt[] results;
        try
        {
            results = await Task.WhenAll(attempts).ConfigureAwait(false);
        }
        catch
        {
            // WhenAll has observed every attempt; successful siblings still own live clients.
            foreach (var task in attempts)
            {
                if (task.Status == TaskStatus.RanToCompletion && task.Result.Client is { } client)
                {
                    _mcpClients.Add(client);
                }
            }

            throw;
        }
        foreach (var result in results)
        {
            if (result.Client is not null) _mcpClients.Add(result.Client);
        }

        _mcpAttempts = results;
    }

    private sealed record McpAttempt(
        IMcpClient? Client,
        string? StartError,
        IReadOnlyList<McpToolDescriptor> Tools,
        string? ListError);

    /// <summary>One server's start and tool listing; cancellation is propagated to the assembler.</summary>
    private async Task<McpAttempt> ConnectMcpAsync(McpServerSettings server, CancellationToken ct)
    {
        IMcpClient? client = null;
        try
        {
            client = McpClientFactory(new McpServerConfig
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = server.Arguments,
                WorkingDirectory = server.WorkingDirectory,
                CallTimeout = TimeSpan.FromSeconds(Math.Max(1, server.CallTimeoutSeconds)),
                StartTimeout = TimeSpan.FromSeconds(Math.Max(1, server.StartTimeoutSeconds)),
            });

            await client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (client is not null) await DisposeMcpClientAsync(client).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (client is not null) await DisposeMcpClientAsync(client).ConfigureAwait(false);
            return new McpAttempt(null, error.Message, Array.Empty<McpToolDescriptor>(), null);
        }

        try
        {
            return new McpAttempt(client, null, await client.ListToolsAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeMcpClientAsync(client).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            return new McpAttempt(client, null, Array.Empty<McpToolDescriptor>(), error.Message);
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

            await DisposeMcpClientAsync(client).ConfigureAwait(false);
        }

        _mcpClients.Clear();
        _mcpAttempts = Array.Empty<McpAttempt>();
        _connectedMcpSettings = null;
        McpNote = null;
    }

    private static async Task DisposeMcpClientAsync(IMcpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"mcp server did not dispose cleanly: {error.Message}");
        }
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
        _buildTurnSnapshot = null;
        Volatile.Write(ref _turnSnapshot, null);

    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _agents.DisposeAsync().ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);
            await StopMcpAsync().ConfigureAwait(false);

            foreach (var client in _subClients.Values)
            {
                client.Dispose();
            }
        }
        finally
        {
            _gate.Release();
            _shutdown.Dispose();
            _gate.Dispose();
        }
    }
}
