using Microsoft.Extensions.Hosting;
using Partner.Api.Services;
using Partner.Api.Settings;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;
using Partner.Api.Auth;
using Partner.Api.Endpoints;
using Partner.Api.Updates;

namespace Partner.Api;

/// <summary>
/// The single composition root: stores, auth middleware, health, view endpoints — in that order. Every host
/// (the executable, the smoke, the tests) builds its app through here, so a test exercises the same wiring as
/// production.
/// </summary>
public static class ApiHost
{
    public static async Task<(WebApplication App, ApiContext Context, ConsumerHost Consumer, VoiceHost Voice, LocalEngineHost LocalEngines, MemoryMaintenanceService Maintenance)> BuildAsync(
        string workspace,
        int port,
        string token,
        TimeProvider? clock = null,
        ILlmClient? llm = null,
        IVoiceComponents? components = null,
        EnginePackage? enginePackage = null,
        UpdateService? updates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Native file writes validate the workspace as a protected directory with strict DOS syntax.
        // Normalize this host-owned path, not paths supplied by the model.
        workspace = Path.GetFullPath(workspace);

        var context = await ApiContext.OpenAsync(workspace, clock, port).ConfigureAwait(false);
        var consumer = ConsumerHost.Create(context, workspace);

        // A job that was running when the process last stopped never reported; say so to the main agent now,
        // before anything new can start.
        await consumer.Agents.RecoverAsync().ConfigureAwait(false);
        var voice = VoiceHost.Create(context, components ?? new WasapiVoiceComponents(), () => consumer.LiveAssistant);
        var package = enginePackage ?? new EnginePackage(EnginePackage.ResolveAppDir(), workspace);
        var localEngines = new LocalEngineHost(package.AppDir);
        localEngines.SetDesired(context.Settings.Load().Local);
        var updateService = updates ?? new UpdateService();
        var voiceStartup = new VoiceStartup(context, voice, localEngines);


        // The store ages whether or not a model is configured: the TTL and promotion rules do not need one.
        var maintenance = new MemoryMaintenanceService(context.Memory, clock);

        if (llm is not null)
        {
            // A caller-supplied client is for tests and diagnostics: it bypasses the settings/env resolution
            // entirely, because a scripted client has no endpoint to describe.
            await consumer.ReconfigureAsync(config: null, "运行中（注入的模型）", llm).ConfigureAwait(false);
        }
        else
        {
            var (config, reason) = ModelConfig.Resolve(context.Settings.Load());
            await consumer.ReconfigureAsync(config, reason).ConfigureAwait(false);
        }

        // Pass-through listeners live on their own loopback ports. Registered as an IHostedService so the host
        // starts them with the app and flushes pending summaries on shutdown, in production and tests alike.
        var services = new ServiceHost(context, workspace, clock ?? TimeProvider.System);

        maintenance.Start();

        // After the consumer is configured, so the first item a due task enqueues has somewhere to go.
        context.StartScheduler();


        var builder = WebApplication.CreateBuilder();
        // Polling makes per-request Information logs noise.
        builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");

        // Shutdown budget, stated rather than assumed: each service flushes its last window to the model on stop,
        // and the parallel stop inside ServiceHost is bounded by its own 20-second deadline. Below this the host
        // would abandon the remaining flushes; above it a wedged stop would hold the process open.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(context.Pet);
        builder.Services.AddSingleton(consumer);
        builder.Services.AddSingleton(voice);
        builder.Services.AddSingleton(localEngines);
        builder.Services.AddSingleton(package);
        builder.Services.AddSingleton(updateService);
        builder.Services.AddSingleton(maintenance);
        builder.Services.AddSingleton(voiceStartup);
        builder.Services.AddSingleton<IHostedService>(voiceStartup);
        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton<IHostedService>(services);

        // The shell serves the UI from its own origin, so every API call is cross-origin. CORS is a
        // browser-enforced allowlist, not the security boundary — the bearer token is. Origins are enumerated
        // rather than wildcarded so a stray page cannot even probe.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(
                "http://tauri.localhost",
                "https://tauri.localhost",
                "tauri://localhost",
                // The browser dev server and the production preview each have an origin of their own.
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:4173",
                "http://127.0.0.1:4173")
            .AllowAnyHeader()
            .AllowAnyMethod()));

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(voiceStartup.Start);

        // Order matters twice over: CORS first so a preflight is answered (preflights carry no credentials and
        // would otherwise be rejected with 401), then the token check before anything is reachable.
        app.UseCors();
        app.UseTokenAuth(token);
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/consumer", (ConsumerHost host) => Results.Ok(host.State));
        app.MapGet("/api/voice", (VoiceHost host) => Results.Ok(host.State));
        app.MapGet("/api/voice/local", (LocalEngineHost host) => Results.Ok(host.State));

        // What the tools have been failing at, most frequent first. A history rather than a state, so it is
        // separate from /api/consumer.
        app.MapGet("/api/tools/failures", (ConsumerHost host, int? take) => Results.Ok(host.Failures(take ?? 10)));
        ViewEndpoints.Map(app, context, workspace);
        PetEndpoints.Map(app, context);
        ServiceEndpoints.Map(app);
        EnginePackageEndpoints.Map(app);
        UpdateEndpoints.Map(app);

        return (app, context, consumer, voice, localEngines, maintenance);
    }
}

/// <summary>Serializes startup and settings changes, cancelling superseded warmups before applying the latest settings.</summary>
public sealed class VoiceStartup : IHostedService
{
    private readonly ApiContext _context;
    private readonly VoiceHost _voice;
    private readonly LocalEngineHost _engines;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancel;
    private Task _work = Task.CompletedTask;
    private bool _started;
    private bool _stopping;

    public VoiceStartup(ApiContext context, VoiceHost voice, LocalEngineHost engines)
    {
        _context = context;
        _voice = voice;
        _engines = engines;
        voice.TrackLocalEngines(engines);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStarted fires only after the server has bound its listener.
        return Task.CompletedTask;
    }

    public void Start()
    {
        lock (_sync) { _started = true; ScheduleLocked(); }
    }

    public Task ReconfigureAsync()
    {
        lock (_sync) { return _started && !_stopping ? ScheduleLocked() : Task.CompletedTask; }
    }

    private Task ScheduleLocked()
    {
        if (_stopping) return Task.CompletedTask;
        var previousCancel = _cancel;
        previousCancel?.Cancel();
        _engines.SetDesired(_context.Settings.Load().Local);
        var previous = _work;
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        var configured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                previousCancel?.Dispose();
                cancel.Token.ThrowIfCancellationRequested();
                var settings = _context.Settings.Load();
                using var refreshGate = new SemaphoreSlim(1, 1);
                (bool Tts, bool Kws)? applied = null;
                async Task RefreshAsync()
                {
                    await refreshGate.WaitAsync(cancel.Token).ConfigureAwait(false);
                    try
                    {
                        var state = _engines.StateFor(settings.Local);
                        var readiness = (state.Tts.Running, state.Kws.Running);
                        if (applied == readiness) return;
                        await _voice.ReconfigureAsync(settings, state, retryFaulted: applied is null).ConfigureAwait(false);
                        applied = readiness;
                    }
                    finally { refreshGate.Release(); }
                }
                await RefreshAsync().ConfigureAwait(false);
                configured.TrySetResult(true);
                await _engines.ReconfigureAsync(settings.Local, cancel.Token, RefreshAsync).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception error) { Console.Error.WriteLine($"Voice startup: {error}"); }
            finally { configured.TrySetResult(false); }
        });
        return configured.Task;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task work;
        CancellationTokenSource? cancel;
        lock (_sync)
        {
            _stopping = true;
            cancel = _cancel;
            _cancel = null;
            cancel?.Cancel();
            work = _work;
        }
        await work.ConfigureAwait(false);
        cancel?.Dispose();
    }
}
