using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Partner.Api.Auth;
using Partner.Api.Voice;

namespace Partner.Api.Tests;

/// <summary>
/// Discovery endpoints: what the UI can offer as a choice instead of asking the user to type it. Both are
/// proxies, because neither the model endpoint nor the audio devices are reachable from the webview.
/// </summary>
public sealed class DiscoveryEndpointTests : IAsyncLifetime
{
    private string _workspace = string.Empty;
    private ApiContext _context = null!;
    private ConsumerHost _consumer = null!;
    private VoiceHost _voice = null!;
    private WebApplication _app = null!;
    private Task _run = null!;
    private HttpClient _http = null!;
    private SyntheticVoice _components = null!;
    private int _port;
    private string _token = string.Empty;

    public async Task InitializeAsync()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "partner-discovery-tests", Guid.NewGuid().ToString("N"));
        _port = FreePort();
        _token = TokenAuth.Generate();
        _components = new SyntheticVoice();

        var (app, context, consumer, voice) = await ApiHost.BuildAsync(
            _workspace, _port, _token, clock: null, llm: null, components: _components);

        _app = app;
        _context = context;
        _consumer = consumer;
        _voice = voice;
        _run = _app.RunAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = await _http.GetAsync("/api/voice/devices");
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
        await _voice.DisposeAsync();
        await _consumer.DisposeAsync();
        await _context.DisposeAsync();

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Devices_are_listed_for_both_directions()
    {
        var devices = await _http.GetFromJsonAsync<JsonElement>("/api/voice/devices");

        var inputs = devices.GetProperty("inputs").EnumerateArray().Select(d => d.GetString()).ToList();
        var outputs = devices.GetProperty("outputs").EnumerateArray().Select(d => d.GetString()).ToList();

        Assert.Equal(new[] { "synthetic-mic", "synthetic-mic-2" }, inputs);
        Assert.Equal(new[] { "synthetic-speaker" }, outputs);
    }

    [Fact]
    public async Task Probing_without_a_model_endpoint_explains_what_is_missing()
    {
        var response = await _http.PostAsJsonAsync("/api/models", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("model.baseUrl", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reachable_endpoint_returns_its_model_ids_sorted()
    {
        using var provider = new FakeModelList(FreePort(), """{"data":[{"id":"qwen3:8b"},{"id":"llama3"},{"id":"qwen3:8b"}]}""");

        var response = await _http.PostAsJsonAsync("/api/models", new { baseUrl = $"http://127.0.0.1:{provider.Port}/v1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var models = body.GetProperty("models").EnumerateArray().Select(m => m.GetString()).ToList();

        // Duplicates collapse and the order is stable, so the dropdown does not shuffle between pulls.
        Assert.Equal(new[] { "llama3", "qwen3:8b" }, models);
        Assert.False(body.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task An_unreachable_endpoint_returns_the_reason_rather_than_an_empty_list()
    {
        var closed = FreePort();

        var response = await _http.PostAsJsonAsync("/api/models", new { baseUrl = $"http://127.0.0.1:{closed}/v1" });

        // 200 with an error field: the probe itself succeeded, the endpoint did not.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("models").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task An_endpoint_without_a_listing_reports_the_provider_status()
    {
        using var provider = new FakeModelList(FreePort(), """{"error":"not found"}""", HttpStatusCode.NotFound);

        var response = await _http.PostAsJsonAsync("/api/models", new { baseUrl = $"http://127.0.0.1:{provider.Port}/v1" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("models").EnumerateArray());
        Assert.Contains("404", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stored_key_only_travels_to_the_stored_endpoint()
    {
        using var stored = new FakeModelList(FreePort(), """{"data":[{"id":"a"}]}""");
        using var elsewhere = new FakeModelList(FreePort(), """{"data":[{"id":"b"}]}""");

        // Configure and store a key for the first endpoint.
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new
            {
                baseUrl = $"http://127.0.0.1:{stored.Port}/v1",
                model = "a",
                apiKey = "stored-key",
            },
        });
        put.EnsureSuccessStatusCode();

        // Probing the stored endpoint sends the key…
        await _http.PostAsJsonAsync("/api/models", new { baseUrl = $"http://127.0.0.1:{stored.Port}/v1" });
        Assert.Equal("Bearer stored-key", stored.LastAuthorization);

        // …and probing a different one does not.
        await _http.PostAsJsonAsync("/api/models", new { baseUrl = $"http://127.0.0.1:{elsewhere.Port}/v1" });
        Assert.Null(elsewhere.LastAuthorization);
    }
}

/// <summary>A minimal OpenAI-compatible listing endpoint over a raw socket.</summary>
internal sealed class FakeModelList : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly string _body;
    private readonly HttpStatusCode _status;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    public FakeModelList(int port, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        Port = port;
        _body = body;
        _status = status;
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string? LastAuthorization { get; private set; }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return;
            }

            using (client)
            {
                await AnswerAsync(client);
            }
        }
    }

    private async Task AnswerAsync(System.Net.Sockets.TcpClient client)
    {
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        await reader.ReadLineAsync();

        while (await reader.ReadLineAsync() is { } header)
        {
            if (header.Length == 0)
            {
                break;
            }

            if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                LastAuthorization = header["Authorization:".Length..].Trim();
            }
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(_body);
        var head = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)_status} {_status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(head);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();

        try
        {
            _serving.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Stopped mid-accept.
        }

        _stop.Dispose();
    }
}
