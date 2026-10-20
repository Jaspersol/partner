using Microsoft.AspNetCore.Builder;
using Partner.Api.Auth;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;
using System.Net.Http.Headers;

namespace Partner.Api.Tests;

/// <summary>
/// A real API host on a free port over a throwaway workspace, so every suite here starts from the same state:
/// a live Kestrel, a bearer token, and a workspace that is deleted when the class is done.
/// </summary>
public abstract class ApiFixture : IAsyncLifetime
{
    protected string _workspace = string.Empty;
    protected ApiContext _context = null!;
    protected ConsumerHost _consumer = null!;
    protected VoiceHost _voice = null!;
    protected LocalEngineHost _localEngines = null!;
    protected WebApplication _app = null!;
    protected Task _run = null!;
    protected HttpClient _http = null!;
    protected int _port;
    protected string _token = string.Empty;

    /// <summary>Names the throwaway directory, so a leftover one says which suite wrote it.</summary>
    protected abstract string WorkspaceName { get; }

    /// <summary>The model the tests run against; null means no model is configured.</summary>
    protected virtual ILlmClient? Llm => null;

    /// <summary>Voice components to inject; null means the real (WASAPI) ones.</summary>
    protected virtual IVoiceComponents? Components => null;

    /// <summary>An endpoint that only answers once the host is listening.</summary>
    protected virtual string ReadinessPath => "/api/consumer";

    public async Task InitializeAsync()
    {
        _workspace = Path.Combine(Path.GetTempPath(), WorkspaceName, Guid.NewGuid().ToString("N"));
        _port = FreePort();
        _token = TokenAuth.Generate();

        var (app, context, consumer, voice, localEngines, _) = await ApiHost.BuildAsync(
            _workspace, _port, _token, clock: null, llm: Llm, components: Components);

        _app = app;
        _context = context;
        _consumer = consumer;
        _voice = voice;
        _localEngines = localEngines;
        _run = _app.RunAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = await _http.GetAsync(ReadinessPath);
                if (probe.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(50);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _run;
        await _localEngines.DisposeAsync();
        await _voice.DisposeAsync();
        await _consumer.DisposeAsync();
        await _context.DisposeAsync();

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    /// <summary>
    /// Asks the OS for a free port, so two suites — or two hosts inside one suite — never collide.
    /// <c>internal</c> because the one suite that builds its host inline rather than through this fixture
    /// still needs a port.
    /// </summary>
    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
