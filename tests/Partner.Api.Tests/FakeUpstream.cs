using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Partner.Api.Tests;

/// <summary>
/// A local stand-in for the upstream model: a real Kestrel host on a free loopback port. Tests supply the handler,
/// which records what arrived and writes whatever response (JSON or SSE) the assertion needs.
/// </summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeUpstream(int port, WebApplication app)
    {
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _app = app;
    }

    public int Port { get; }
    public string BaseUrl { get; }

    public static async Task<FakeUpstream> StartAsync(Func<HttpContext, Task> handler, CancellationToken ct = default)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");
        var app = builder.Build();

        // The proxy appends /chat/completions (OpenAI) or /v1/messages (Anthropic) to the configured base URL.
        app.MapPost("/chat/completions", handler);
        app.MapPost("/v1/chat/completions", handler);
        app.MapPost("/v1/messages", handler);
        app.MapPost("/messages", handler);

        await app.StartAsync(ct).ConfigureAwait(false);
        return new FakeUpstream(port, app);
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
