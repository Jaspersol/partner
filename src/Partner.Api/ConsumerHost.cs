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
/// </summary>
public sealed class ConsumerHost : IAsyncDisposable
{
    private readonly TurnLoop? _loop;
    private readonly CancellationTokenSource? _stop;
    private readonly Task? _running;
    private readonly string _reason;
    private readonly string? _model;

    private ConsumerHost(TurnLoop? loop, CancellationTokenSource? stop, Task? running, string reason, string? model)
    {
        _loop = loop;
        _stop = stop;
        _running = running;
        _reason = reason;
        _model = model;
    }

    public ConsumerState State => new(
        Running: _running is { IsCompleted: false },
        Reason: _reason,
        Model: _model,
        Stats: _loop?.Stats ?? TurnLoopStats.Empty);

    public static ConsumerHost Disabled(string reason) => new(null, null, null, reason, null);

    public static ConsumerHost Start(ApiContext context, string workspace, ILlmClient llm)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(llm);

        var runner = new AgentTurnRunner(
            context.Session,
            llm,
            new InMemoryToolRegistry(),
            // Nothing is exposed to the model yet, so an empty allow-list denies exactly nothing. It is the
            // fail-closed default: a tool must be registered *and* allowed before it can run.
            new AllowListGovernance(Array.Empty<string>()),
            new DenyAllApprovalHandler(),
            SkillHooks(workspace));

        var loop = new TurnLoop(context.Queue, runner, context.Session);
        var stop = new CancellationTokenSource();

        return new ConsumerHost(
            loop,
            stop,
            loop.RunAsync(stop.Token),
            "运行中",
            llm is HttpLlmClient http ? http.Model : llm.GetType().Name);
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
    public async ValueTask DisposeAsync()
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
    }
}
