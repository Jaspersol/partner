using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>
/// The page's debug protocol, spoken to a stand-in for a browser: a loopback HTTP listener that serves the
/// target list, upgrades the page socket, answers every command and records what it was sent. This pins the
/// commands and their order on the wire, the one-time focus emulation, a refusal reported as a refusal, and an
/// unreachable page reported as unreachable, without a browser or a window.
/// </summary>
public sealed class CdpInputWireTests
{
    private sealed record Received(string Method, JsonElement Params);

    private sealed class FakeDebugTarget : IAsyncDisposable
    {
        private readonly HttpListener _http = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Received> _received = new();
        private readonly object _gate = new();

        public FakeDebugTarget(string? failMethod = null)
        {
            FailMethod = failMethod;
            Port = CdpRegistry.FreePort();
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        /// <summary>A command the stand-in answers with a protocol error, as a page refusing it would.</summary>
        public string? FailMethod { get; }

        public IReadOnlyList<Received> Commands
        {
            get
            {
                lock (_gate)
                {
                    return _received
                        .Where(command => command.Method != "Runtime.enable" && command.Method != "Runtime.evaluate")
                        .ToList();
                }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _http.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_http.IsListening)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(context));
            }
        }

        private async Task ServeAsync(HttpListenerContext context)
        {
            if (!context.Request.IsWebSocketRequest)
            {
                var body = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        type = "page",
                        title = "stand-in",
                        url = "http://stand-in/",
                        webSocketDebuggerUrl = $"ws://127.0.0.1:{Port}/devtools/page/1",
                    },
                });

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            var socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
            var buffer = new byte[64 * 1024];

            while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                using var request = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var id = request.RootElement.GetProperty("id").GetInt32();
                var method = request.RootElement.GetProperty("method").GetString()!;
                var parameters = request.RootElement.TryGetProperty("params", out var found) ? found.Clone() : default;

                lock (_gate)
                {
                    _received.Add(new Received(method, parameters));
                }

                var reply = method == FailMethod
                    ? $"{{\"id\":{id},\"error\":{{\"code\":-32000,\"message\":\"refused by the page\"}}}}"
                    : method == "Runtime.evaluate"
                        ? $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"object\",\"value\":{{\"w\":1000,\"h\":640,\"dpr\":1.5}}}}}}}}"
                        : $"{{\"id\":{id},\"result\":{{}}}}";

                await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _http.Close();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_click_reaches_the_page_as_a_press_and_release_after_the_page_is_told_it_is_focused()
    {
        await using var target = new FakeDebugTarget();
        var source = new CdpDomSource();

        var result = await source.ClickAsync(target.Port, "stand-in", 120.5, 40, right: false, count: 1);

        Assert.NotNull(result);
        Assert.True(result!.Ok, result.Detail);
        Assert.Equal("cdp.click-at", result.Method);

        Assert.Equal(
            new[]
            {
                "Emulation.setFocusEmulationEnabled",
                "Input.dispatchMouseEvent",
                "Input.dispatchMouseEvent",
                "Input.dispatchMouseEvent",
            },
            target.Commands.Select(command => command.Method).ToArray());

        var types = target.Commands.Skip(1).Select(command => command.Params.GetProperty("type").GetString()).ToArray();
        Assert.Equal(new[] { "mouseMoved", "mousePressed", "mouseReleased" }, types);
        Assert.Equal(120.5, target.Commands[2].Params.GetProperty("x").GetDouble());
    }

    [Fact]
    public async Task The_page_is_told_it_is_focused_once_per_connection_not_once_per_action()
    {
        await using var target = new FakeDebugTarget();
        var source = new CdpDomSource();

        await source.MoveAsync(target.Port, "stand-in", 1, 1);
        await source.MoveAsync(target.Port, "stand-in", 2, 2);

        Assert.Single(target.Commands, command => command.Method == "Emulation.setFocusEmulationEnabled");
    }

    [Fact]
    public async Task Text_goes_in_as_one_insertion_not_as_keystrokes()
    {
        await using var target = new FakeDebugTarget();
        var source = new CdpDomSource();

        var result = await source.TypeAsync(target.Port, "stand-in", "你好 world");

        Assert.True(result!.Ok, result.Detail);

        var insertion = Assert.Single(target.Commands, command => command.Method == "Input.insertText");
        Assert.Equal("你好 world", insertion.Params.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_page_that_refuses_a_command_is_reported_as_a_refusal_with_its_reason()
    {
        await using var target = new FakeDebugTarget(failMethod: "Input.dispatchMouseEvent");
        var source = new CdpDomSource();

        var result = await source.ClickAsync(target.Port, "stand-in", 10, 10, right: false, count: 1);

        // Not null: the channel was up and the page said no, which is an answer. Null means the page is gone.
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Contains("refused by the page", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Input.dispatchMouseEvent", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_nothing_is_listening_on_is_a_channel_that_is_down_not_a_refusal()
    {
        var source = new CdpDomSource();

        var result = await source.TypeAsync(CdpRegistry.FreePort(), "stand-in", "x");

        Assert.Null(result);
    }

    [Fact]
    public async Task A_key_the_protocol_cannot_name_never_reaches_the_page()
    {
        await using var target = new FakeDebugTarget();
        var source = new CdpDomSource();

        var result = await source.PressKeyAsync(target.Port, "stand-in", 0xFF, Array.Empty<ushort>());

        Assert.False(result!.Ok);
        Assert.Empty(target.Commands);
    }

    [Fact]
    public async Task The_viewport_is_read_from_the_page_at_the_moment_of_the_action()
    {
        await using var target = new FakeDebugTarget();
        var source = new CdpDomSource();

        var viewport = await source.ViewportAsync(target.Port, "stand-in");

        Assert.NotNull(viewport);
        Assert.Equal(1000, viewport!.InnerWidth);
        Assert.Equal(640, viewport.InnerHeight);
        Assert.Equal(1.5, viewport.DevicePixelRatio);
    }
}
