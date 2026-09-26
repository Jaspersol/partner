using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Skills;
using Partner.Core.Agent.Tools;
using Partner.Core.Hosting;

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
public sealed class ConsumerHost : IAsyncDisposable
{
    private readonly ApiContext _context;
    private readonly string _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TurnLoop? _loop;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private HttpClient? _http;
    private string _reason = "未启动";
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
        }
        else
        {
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            llm = new HttpLlmClient(_http, new HttpLlmOptions
            {
                BaseUrl = config!.BaseUrl,
                Model = config.Model,
                ApiKey = config.ApiKey,
                Timeout = config.Timeout,
            });
        }

        var runner = new AgentTurnRunner(
            _context.Session,
            llm,
            new InMemoryToolRegistry(),
            // Nothing is exposed to the model yet, so an empty allow-list denies exactly nothing. It is the
            // fail-closed default: a tool must be registered *and* allowed before it can run.
            new AllowListGovernance(Array.Empty<string>()),
            new DenyAllApprovalHandler(),
            SkillHooks(_workspace));

        _loop = new TurnLoop(_context.Queue, runner, _context.Session);
        _stop = new CancellationTokenSource();
        _running = _loop.RunAsync(_stop.Token);
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
