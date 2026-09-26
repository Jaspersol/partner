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
    public static async Task<(WebApplication App, ApiContext Context, ConsumerHost Consumer)> BuildAsync(
        string workspace,
        int port,
        string token,
        TimeProvider? clock = null,
        ILlmClient? llm = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var context = await ApiContext.OpenAsync(workspace, clock).ConfigureAwait(false);

        // A caller-supplied client is for tests and diagnostics; otherwise the environment decides. Either way
        // the reason travels with the host so a disabled consumer is never silent.
        var (configured, reason) = llm is null
            ? HttpLlmClient.TryFromEnvironment()
            : ((ILlmClient?)llm, "已注入模型（测试或诊断）");

        var consumer = configured is null
            ? ConsumerHost.Disabled(reason)
            : ConsumerHost.Start(context, workspace, configured);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(consumer);

        // The shell serves the UI from its own origin, so every API call is cross-origin. CORS here is a
        // browser-enforced allowlist, not the security boundary — the bearer token is (and CORS never grants
        // access without it). Origins are enumerated rather than wildcarded so a stray page cannot even probe.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(
                "http://tauri.localhost",
                "https://tauri.localhost",
                "tauri://localhost",
                "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()));

        var app = builder.Build();

        // Order matters twice over: CORS first so a preflight is answered (preflights carry no credentials and
        // would otherwise be rejected with 401), then the token check before anything is reachable.
        app.UseCors();
        app.UseTokenAuth(token);
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/consumer", (ConsumerHost host) => Results.Ok(host.State));
        ViewEndpoints.Map(app);

        return (app, context, consumer);
    }
}
