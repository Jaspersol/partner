using Microsoft.Extensions.Hosting;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Services;

/// <summary>One configured service plus its live state, as returned by the management endpoints.</summary>
public sealed record ServiceListItem(ServiceSettingsView Settings, ServiceState State);

/// <summary>
/// Owns every service instance. Registered as a singleton and an <see cref="IHostedService"/> so the host starts
/// and stops the listeners with the app (production and tests alike); stopping flushes pending rounds first.
/// </summary>
public sealed class ServiceHost : IHostedService, IAsyncDisposable
{
    private readonly ApiContext _ctx;
    private readonly string _workspace;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ServiceInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>
    /// Ids whose instance is being started outside the gate. Prevents a second reconcile from starting the same
    /// instance twice while the first is still awaiting its bind.
    /// </summary>
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// How long the whole parallel stop may spend flushing to the model. Past it the remaining instances are
    /// written with the template, which needs no model. Settable so its own test does not have to wait 20 seconds.
    /// </summary>
    internal TimeSpan ShutdownFlushBudget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Instances stopped by <see cref="StopAsync"/>. They stay alive (the listener is down, the object is not
    /// disposed) so a test can still read their final buffer, and disposal cleans them up.
    /// </summary>
    private readonly List<ServiceInstance> _stopped = new();

    private bool _disposed;

    public ServiceHost(ApiContext ctx, string workspace, TimeProvider clock)
    {
        _ctx = ctx;
        _workspace = workspace;
        _clock = clock;
        // No global timeout: a stream is bounded by the client's RequestAborted, and the summarizer uses its own
        // 30s token. A 100s default would silently cut off a long SSE response.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task StartAsync(CancellationToken ct) =>
        await ReconcileAsync(new ServicesSettings(), _ctx.Settings.Load().Services, ct).ConfigureAwait(false);

    /// <summary>
    /// Parallel, with a total budget: N instances × a 30-second model call would exceed the host's shutdown
    /// window, and the ones that did not fit would be flushed a second time by disposal. Past the budget the
    /// remaining instances are written with the template, which needs no model.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        List<ServiceInstance> instances;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            instances = _instances.Values.ToList();
            _instances.Clear();
            _stopped.AddRange(instances);
        }
        finally
        {
            _gate.Release();
        }

        var budget = ShutdownFlushBudget;
        var flushes = instances.Select(instance => StopOneAsync(instance, budget, ct)).ToList();
        await Task.WhenAll(flushes).ConfigureAwait(false);
    }

    private static async Task StopOneAsync(ServiceInstance instance, TimeSpan budget, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(budget);
        try
        {
            await instance.StopAsync(flush: true, templateOnly: false, timer.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Out of budget: write the window with the template instead of leaving it for the next start. The
            // template needs no model, so this completes in milliseconds.
        }

        await instance.StopAsync(flush: true, templateOnly: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the running listeners in line with the saved config. Added / enabled → start; removed / disabled →
    /// flush then stop; port or protocol changed → rebuild the listener; anything else is a hot update that the
    /// next request sees.
    /// <para>
    /// The lock only guards the dictionary. Stopping and starting happen outside it, because a stop flushes to the
    /// model (up to 30 seconds) and holding the gate that long would block <c>GET /api/services</c>.
    /// </para>
    /// </summary>
    public async Task ReconcileAsync(ServicesSettings before, ServicesSettings after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(after);
        var afterById = after.Instances.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var stopping = new List<ServiceInstance>();
        var starting = new List<ServiceSettings>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (id, instance) in _instances.ToList())
            {
                var next = afterById.GetValueOrDefault(id);
                var enabled = next is { Enabled: true };
                var rebuild = enabled
                    && (next!.Port != instance.Settings.Port
                        || !string.Equals(
                            LlmProtocols.Normalize(next.Protocol),
                            LlmProtocols.Normalize(instance.Settings.Protocol),
                            StringComparison.Ordinal));

                if (!enabled || rebuild)
                {
                    _instances.Remove(id);
                    stopping.Add(instance);
                }
                else
                {
                    instance.UpdateSettings(next!);
                }
            }

            // Reserve the ids now, so a concurrent state read reports "not running" instead of a gap, and a
            // concurrent reconcile cannot start the same instance twice.
            foreach (var settings in after.Instances)
            {
                if (!settings.Enabled || _instances.ContainsKey(settings.Id) || !_pending.Add(settings.Id))
                {
                    continue;
                }

                starting.Add(settings);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Stop first: a rebuilt instance may need the port the old one still holds.
        foreach (var instance in stopping)
        {
            await instance.StopAsync(flush: true, templateOnly: false, ct).ConfigureAwait(false);
            await instance.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var settings in starting)
        {
            ServiceInstance? created = null;
            try
            {
                created = new ServiceInstance(settings, _ctx, _http, _workspace, _clock);
                await created.StartAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _pending.Remove(settings.Id);
                    if (created is not null)
                    {
                        _instances[settings.Id] = created;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }

    public async Task<IReadOnlyList<ServiceListItem>> StateAsync(CancellationToken ct)
    {
        var configured = _ctx.Settings.Load().Services.Instances;

        // Snapshot the dictionary under the gate, then read each instance's state outside it: a state read must
        // answer while a flush or a reconcile is still running.
        var live = new Dictionary<string, ServiceInstance>(StringComparer.Ordinal);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (id, instance) in _instances)
            {
                live[id] = instance;
            }
        }
        finally
        {
            _gate.Release();
        }

        var result = new List<ServiceListItem>(configured.Count);
        foreach (var service in configured)
        {
            result.Add(live.TryGetValue(service.Id, out var instance)
                ? new ServiceListItem(
                    ServiceSettingsView.From(instance.Settings),
                    await instance.StateAsync(ct).ConfigureAwait(false))
                : new ServiceListItem(
                    ServiceSettingsView.From(service),
                    new ServiceState(false, null, service.Port, 0, null, 0, null, null)));
        }

        return result;
    }

    public async Task<ServiceListItem?> GetAsync(string id, CancellationToken ct)
    {
        var configured = _ctx.Settings.Load().Services.Instances.FirstOrDefault(s => s.Id == id);
        if (configured is null)
        {
            return null;
        }

        ServiceInstance? instance;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _instances.TryGetValue(id, out instance);
        }
        finally
        {
            _gate.Release();
        }

        return instance is null
            ? new ServiceListItem(
                ServiceSettingsView.From(configured),
                new ServiceState(false, null, configured.Port, 0, null, 0, null, null))
            : new ServiceListItem(
                ServiceSettingsView.From(instance.Settings),
                await instance.StateAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Immediate summarize (POST /flush). The instance is looked up under the gate and then released, so the
    /// management endpoints keep answering during the model call.
    /// </summary>
    public async Task<string?> FlushAsync(string id, CancellationToken ct)
    {
        ServiceInstance? instance;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _instances.TryGetValue(id, out instance);
        }
        finally
        {
            _gate.Release();
        }

        return instance is null
            ? null
            : await instance.FlushAsync(force: true, templateOnly: false, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Shutdown must not throw from a failed flush.
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var instance in _instances.Values)
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
            _instances.Clear();

            foreach (var instance in _stopped)
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
            _stopped.Clear();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _http.Dispose();
    }
}
