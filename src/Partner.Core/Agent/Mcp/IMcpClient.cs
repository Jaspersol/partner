namespace Partner.Core.Agent.Mcp;

/// <summary>
/// A connection to one MCP server: lifecycle, capability discovery, and tool calls.
/// <para>
/// Implementations must guarantee: a call made while the process is dead or not started throws
/// <see cref="InvalidOperationException"/> immediately (never hangs), and a single request that exceeds
/// <see cref="McpServerConfig.CallTimeout"/> throws <see cref="TimeoutException"/>.
/// </para>
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>Whether the child process is alive (<see langword="false"/> when stopped or exited).</summary>
    bool IsRunning { get; }

    /// <summary>The server name from the configuration.</summary>
    string ServerName { get; }

    /// <summary>Starts the child process and completes the <c>initialize</c> handshake and the <c>notifications/initialized</c> notification.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists the tools the server exposes; repeated calls within <see cref="McpServerConfig.CapabilityCacheTtl"/>
    /// return the cache and issue no new <c>tools/list</c> request.
    /// </summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>Calls one tool; protocol errors and <c>isError</c> are both returned through <see cref="McpCallResult"/>.</summary>
    Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default);

    /// <summary>Closes stdin, waits up to 2 seconds for the child to exit, then kills the process tree; safe to call repeatedly.</summary>
    Task StopAsync(CancellationToken ct = default);
}
