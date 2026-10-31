using System.Diagnostics;
using Partner.Core.Agent.Mcp;
using Partner.Core.Agent.Tools;
using Partner.Core.Tests.Sessions;

namespace Partner.Core.Tests.Agent.Mcp;

public sealed class StdioMcpClientTests : IDisposable
{
    private const string Configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "partner-mcp-tests", Guid.NewGuid().ToString("n"));

    public StdioMcpClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed temp-directory cleanup must not fail the test.
        }
    }

    [Fact]
    public async Task Start_handshakes_and_lists_the_three_tools_in_order()
    {
        await using var client = new StdioMcpClient(Config(LogPath));

        await client.StartAsync();

        Assert.True(client.IsRunning);
        Assert.Equal("test-mcp", client.ServerName);

        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "echo", "fail", "hang" }, tools.Select(t => t.Name).ToArray());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Contains("\"type\"", tools[0].InputSchemaJson);
    }

    [Fact]
    public async Task CallTool_maps_success_failure_and_unknown_tool()
    {
        await using var client = new StdioMcpClient(Config(LogPath));
        await client.StartAsync();

        var echo = await client.CallToolAsync("echo", "{\"text\":\"hi\"}");
        Assert.True(echo.Ok);
        Assert.False(echo.IsError);
        Assert.Equal("hi", echo.Content);

        var fail = await client.CallToolAsync("fail", "{}");
        Assert.False(fail.Ok);
        Assert.True(fail.IsError);

        var unknown = await client.CallToolAsync("nope", "{}");
        Assert.False(unknown.Ok);
        Assert.True(unknown.IsError);
        Assert.Contains("nope", unknown.Content);
    }

    [Fact]
    public async Task ListTools_is_cached_until_the_ttl_expires()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var log = LogPath;
        await using var client = new StdioMcpClient(
            Config(log, ttl: TimeSpan.FromSeconds(10)), clock);

        await client.StartAsync();

        await client.ListToolsAsync();
        await WaitForLogAsync(log, "tools/list", 1);

        await client.ListToolsAsync();
        await Task.Delay(200);
        Assert.Equal(1, CountLogLines(log, "tools/list"));

        clock.Advance(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(1));
        await client.ListToolsAsync();
        await WaitForLogAsync(log, "tools/list", 2);
        Assert.Equal(2, CountLogLines(log, "tools/list"));
    }

    [Fact]
    public async Task Hang_times_out_with_the_method_name_and_the_client_stays_usable()
    {
        // The same timeout covers the stdio handshake; allow for a loaded `dotnet` start.
        await using var client = new StdioMcpClient(
            Config(LogPath, callTimeout: TimeSpan.FromSeconds(1)));
        await client.StartAsync();

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(
            async () => await client.CallToolAsync("hang", "{}"));
        stopwatch.Stop();

        Assert.Contains("tools/call", error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"timed out too slowly: {stopwatch.Elapsed}");

        Assert.True(client.IsRunning);
        var echo = await client.CallToolAsync("echo", "{\"text\":\"still alive\"}");
        Assert.True(echo.Ok);
        Assert.Equal("still alive", echo.Content);
    }

    [Fact]
    public async Task Stop_is_idempotent_and_calls_after_stop_fail_fast()
    {
        var client = new StdioMcpClient(Config(LogPath));
        await client.StartAsync();
        Assert.True(client.IsRunning);

        await client.StopAsync();
        Assert.False(client.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CallToolAsync("echo", "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ListToolsAsync());

        await client.StopAsync();
        Assert.False(client.IsRunning);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_calls_each_match_their_own_payload()
    {
        await using var client = new StdioMcpClient(Config(LogPath));
        await client.StartAsync();

        var payloads = Enumerable.Range(0, 5).Select(i => $"payload-{i}").ToArray();
        var results = await Task.WhenAll(
            payloads.Select(p => client.CallToolAsync("echo", $"{{\"text\":\"{p}\"}}")));

        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.True(results[i].Ok);
            Assert.Equal(payloads[i], results[i].Content);
        }
    }

    [Fact]
    public async Task McpTool_bridges_the_descriptor_and_the_call_result()
    {
        await using var client = new StdioMcpClient(Config(LogPath));
        await client.StartAsync();

        var tools = await client.ListToolsAsync();
        var echoDescriptor = tools.Single(t => t.Name == "echo");
        var echo = new McpTool(client, echoDescriptor);

        Assert.Equal("echo", echo.Spec.Name);
        Assert.Equal(echoDescriptor.Description, echo.Spec.Description);
        Assert.Equal(echoDescriptor.InputSchemaJson, echo.Spec.ParametersJsonSchema);

        var ok = await echo.InvokeAsync("{\"text\":\"bridged\"}");
        Assert.True(ok.Ok);
        Assert.Equal("bridged", ok.Content);

        var fail = new McpTool(client, tools.Single(t => t.Name == "fail"));
        var failed = await fail.InvokeAsync("{}");
        Assert.False(failed.Ok);
        Assert.Equal("tool failed on purpose", failed.Content);
    }

    [Fact]
    public async Task Handshake_logs_initialize_then_initialized()
    {
        var log = LogPath;
        await using var client = new StdioMcpClient(Config(log));

        await client.StartAsync();

        var lines = await WaitForLogAsync(log, method: null, count: 2);
        Assert.True(lines.Length >= 2, $"expected at least 2 logged methods, got: {string.Join(",", lines)}");
        Assert.Equal("initialize", lines[0]);
        Assert.Equal("notifications/initialized", lines[1]);
    }

    private string LogPath => Path.Combine(_dir, "mcp.log");

    private static McpServerConfig Config(string logPath, TimeSpan? callTimeout = null, TimeSpan? ttl = null) => new()
    {
        Name = "test-mcp",
        Command = "dotnet",
        Arguments = new[] { TestServerDll() },
        CallTimeout = callTimeout ?? TimeSpan.FromSeconds(30),
        CapabilityCacheTtl = ttl ?? TimeSpan.FromSeconds(10),
        Environment = new Dictionary<string, string> { ["PARTNER_MCP_TEST_LOG"] = logPath },
    };

    private static string TestServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not find 'Partner.sln' above '{AppContext.BaseDirectory}'; " +
                "cannot locate the MCP test-double server.");
        }

        // Use the newest build; the framework folder name changes with the TFM.
        var binRoot = Path.Combine(
            directory.FullName, "tests", "Partner.McpTestServer", "bin", Configuration);

        var dll = Directory.Exists(binRoot)
            ? Directory.GetFiles(binRoot, "Partner.McpTestServer.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (dll is null)
        {
            throw new InvalidOperationException(
                $"MCP test-double server is not built under '{binRoot}'. " +
                "Run `dotnet build` on the solution before running these tests.");
        }

        return dll;
    }

    private static int CountLogLines(string path, string method) =>
        ReadLogLines(path).Count(line => line == method);

    private static async Task<string[]> WaitForLogAsync(
        string path, string? method, int count, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var lines = ReadLogLines(path);
            var matches = method is null ? lines.Length : lines.Count(line => line == method);
            if (matches >= count || Environment.TickCount64 > deadline)
            {
                return lines;
            }

            await Task.Delay(25);
        }
    }

    private static string[] ReadLogLines(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            // The child is still appending; read again on the next pass.
            return Array.Empty<string>();
        }
    }
}
