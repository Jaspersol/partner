using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Skills;
using Partner.Core.Agent.Tools;
using Partner.Core.Hosting;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Api;

/// <summary>What the consumer is doing, so "nothing is executing" is a visible state rather than a silent one.</summary>
public sealed record ConsumerState(bool Running, string Reason, string? Model, TurnLoopStats Stats);

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
    private string _reason = "未启动";

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
    }

    public ConsumerState State => new(
        Running: _running is { IsCompleted: false },
        Reason: _reason,
        Model: _model,
        Stats: _loop?.Stats ?? TurnLoopStats.Empty);

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
            // A new item starts a new live text; the old one belongs to a step that already ended.
            _live = _live is { } current && current.ItemId == itemId
                ? current with { Text = current.Text + delta }
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
            await StopAsync().ConfigureAwait(false);

            if (config is null && injected is null)
            {
                _reason = reason;
                _model = null;
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
            });

            llm = client;

            // Two arms: the model's own tokenizer where the server has one, the local estimate as the floor.
            Tokens = new DualArmTokenCounter(client, new ApproximateTokenCounter());
        }

        var turnOptions = new AgentTurnOptions
        {
            ContextWindowTokens = config?.ContextTokens ?? 32768,
            MaxTokens = config?.MaxTokens ?? 4096,
        };

        // Built here rather than left to the runner's default: the assembler is the one that can reach the
        // media store, so it is the one that attaches the input's images.
        var assembler = new SessionWindowContextAssembler(
            _context.Session,
            turnOptions.ContextWindowTokens,
            turnOptions.MaxTokens,
            Tokens,
            turnOptions.BaseSystemPrompt,
            _context.Media);

        var runner = new AgentTurnRunner(
            _context.Session,
            llm,
            new InMemoryToolRegistry(),
            // Nothing is exposed to the model yet, so an empty allow-list denies exactly nothing. It is the
            // fail-closed default: a tool must be registered *and* allowed before it can run.
            new AllowListGovernance(Array.Empty<string>()),
            new DenyAllApprovalHandler(),
            SkillHooks(_workspace),
            assembler: assembler,
            options: turnOptions,
            stream: this);

        _loop = new TurnLoop(_context.Queue, runner, _context.Session);
        _stop = new CancellationTokenSource();
        _running = _loop.RunAsync(_stop.Token);
        _llm = llm;
        _model = injected is not null ? injected.GetType().Name : config!.Model;
        _reason = reason;
    }

    /// <summary>Skills already in the workspace are injected; nothing is fabricated when there are none.</summary>
    private static HookRegistry SkillHooks(string workspace)
    {
        var hooks = new HookRegistry();
        var loaded = new SkillLoader().Load(workspace);

        if (loaded.Skills.Count > 0)
        {
            var registry = new InMemorySkillRegistry();
            registry.Reload(loaded.Skills);
            hooks.Register(new SkillInjectionHook(registry));
        }

        return hooks;
    }

    /// <summary>
    /// Stops claiming new work and waits for the turn in flight. An interrupted item is re-queued by the queue
    /// on the next start, so this is a graceful stop rather than a kill.
    /// </summary>
    private async Task StopAsync()
    {
        if (_stop is null || _running is null)
        {
            return;
        }

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

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
