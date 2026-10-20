namespace Partner.Core.Agent.Mcp;

/// <summary>
/// Configuration for an MCP server started over stdio.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>Server name, used in logs and error messages and as the namespace in the registry.</summary>
    public required string Name { get; init; }

    /// <summary>Executable name or path, for example <c>dotnet</c>.</summary>
    public required string Command { get; init; }

    /// <summary>Command-line arguments, passed one by one and never parsed by a shell.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Working directory for the child process; <see langword="null"/> inherits the current process's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Key/value pairs added to or overriding the child process's environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Timeout for one request (initialize / tools/list / tools/call).</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a tools/list result stays valid (10s).</summary>
    public TimeSpan CapabilityCacheTtl { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>One tool descriptor returned by an MCP server's <c>tools/list</c>.</summary>
/// <param name="Name">Tool name.</param>
/// <param name="Description">Usage description for the model.</param>
/// <param name="InputSchemaJson">JSON Schema for the arguments, as raw JSON text.</param>
public sealed record McpToolDescriptor(string Name, string Description, string InputSchemaJson);

/// <summary>The result of one MCP <c>tools/call</c>.</summary>
/// <param name="Ok">Whether the call succeeded (a protocol error or <c>isError</c> makes this <see langword="false"/>).</param>
/// <param name="Content">Joined text content, or the error description on failure.</param>
/// <param name="IsError">Whether the tool or the protocol explicitly reported an error.</param>
public sealed record McpCallResult(bool Ok, string Content, bool IsError);
