using Partner.Core.Agent.Mcp;
using Partner.Core.Agent.Tools;

namespace Partner.Api;

/// <summary>
/// One MCP server's tool, under a name no other server can take.
/// <para>
/// MCP tool names are chosen by each server and nothing stops two servers from choosing the same one, so
/// registering them verbatim means whichever loads last silently replaces the other — and the model then calls
/// whichever survived without ever knowing there was a choice. Prefixing with the server name makes that
/// collision impossible and says where a tool came from, which matters when one of them misbehaves.
/// </para>
/// <para>
/// Everything else is <see cref="McpTool"/>: the call is forwarded untouched, so the server sees the name it
/// published, and governance still applies on the way through.
/// </para>
/// </summary>
public sealed class NamespacedMcpTool : ITool
{
    private readonly McpTool _inner;

    public NamespacedMcpTool(IMcpClient client, string server, McpToolDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentNullException.ThrowIfNull(descriptor);

        _inner = new McpTool(client, descriptor);
        Spec = new ToolSpec(
            Name(server, descriptor.Name),
            $"[{server}] {descriptor.Description}",
            descriptor.InputSchemaJson);
    }

    public ToolSpec Spec { get; }

    /// <summary>The name the model sees, and the only one it may call back with.</summary>
    public static string Name(string server, string tool) => $"mcp_{server}_{tool}";

    /// <summary>
    /// Recovers the server and the tool a qualified name belongs to.
    /// <para>
    /// Needed because the split is not unambiguous by construction: a server name may itself contain an
    /// underscore, so the separator is the <b>first</b> one after the server prefix and everything past it is the
    /// tool name. The registry is what resolves the rest — a name that does not match a running server is
    /// simply not a match.
    /// </para>
    /// </summary>
    public static bool TryUnprefix(string qualified, out string server, out string tool)
    {
        server = string.Empty;
        tool = string.Empty;

        const string Prefix = "mcp_";
        if (!qualified.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = qualified[Prefix.Length..];
        var at = rest.IndexOf('_', StringComparison.Ordinal);
        if (at <= 0 || at == rest.Length - 1)
        {
            return false;
        }

        server = rest[..at];
        tool = rest[(at + 1)..];
        return true;
    }

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        _inner.InvokeAsync(argumentsJson, ct);
}
