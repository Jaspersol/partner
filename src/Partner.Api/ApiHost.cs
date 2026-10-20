using Partner.Api.Settings;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;
using Partner.Api.Auth;
using Partner.Api.Endpoints;

namespace Partner.Api;

/// <summary>
/// The single composition root: stores, auth middleware, health, view endpoints — in that order. Every host
/// (the executable, the smoke, the tests) builds its app through here, so a test can never end up exercising
/// a different wiring than production. That is exactly the gap that would otherwise hide a missing auth
/// middleware behind green tests.
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
        EnginePackage? enginePackage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var context = await ApiContext.OpenAsync(workspace, clock).ConfigureAwait(false);
        var consumer = ConsumerHost.Create(context, workspace);

        // A job that was running when the process last stopped never reported; say so to the main agent now,
        // before anything new can start.
        await consumer.Agents.RecoverAsync().ConfigureAwait(false);
        var voice = VoiceHost.Create(context, components ?? new WasapiVoiceComponents(), () => consumer.LiveAssistant);
        var package = enginePackage ?? new EnginePackage(EnginePackage.ResolveAppDir(), workspace);
        var localEngines = new LocalEngineHost(package.AppDir);

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

        maintenance.Start();

        // After the consumer is configured, so the first item a due task enqueues has somewhere to go.
        context.StartScheduler();

        await localEngines.ReconfigureAsync(context.Settings.Load().Local).ConfigureAwait(false);

        await voice.ReconfigureAsync(context.Settings.Load()).ConfigureAwait(false);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(consumer);
        builder.Services.AddSingleton(voice);
        builder.Services.AddSingleton(localEngines);
        builder.Services.AddSingleton(package);
        builder.Services.AddSingleton(maintenance);

        // The shell serves the UI from its own origin, so every API call is cross-origin. CORS here is a
        // browser-enforced allowlist, not the security boundary — the bearer token is (and CORS never grants
        // access without it). Origins are enumerated rather than wildcarded so a stray page cannot even probe.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(
                "http://tauri.localhost",
                "https://tauri.localhost",
                "tauri://localhost",
                // The browser dev server and the production preview each have an origin of their own. Without
                // them a plain-browser session reads as "every request failed" rather than "wrong origin".
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:4173",
                "http://127.0.0.1:4173")
            .AllowAnyHeader()
            .AllowAnyMethod()));

        var app = builder.Build();

        // Order matters twice over: CORS first so a preflight is answered (preflights carry no credentials and
        // would otherwise be rejected with 401), then the token check before anything is reachable.
        app.UseCors();
        app.UseTokenAuth(token);
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/consumer", (ConsumerHost host) => Results.Ok(host.State));
        app.MapGet("/api/voice", (VoiceHost host) => Results.Ok(host.State));
        app.MapGet("/api/voice/local", (LocalEngineHost host) => Results.Ok(host.State));

        // What the tools have been failing at, most frequent first. Separate from /api/consumer because this
        // is a history rather than a state, and a caller refreshing a badge has no reason to read it.
        app.MapGet("/api/tools/failures", (ConsumerHost host, int? take) => Results.Ok(host.Failures(take ?? 10)));
        ViewEndpoints.Map(app, context, workspace);
        EnginePackageEndpoints.Map(app);

        return (app, context, consumer, voice, localEngines, maintenance);
    }
}
