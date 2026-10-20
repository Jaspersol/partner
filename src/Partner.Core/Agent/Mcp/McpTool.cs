using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Mcp;

/// <summary>Exposes one MCP tool through the local ITool registry, so governance applies to it too.</summary>
public sealed class McpTool : ITool
{
    private readonly IMcpClient _client;

    /// <param name="client">A started MCP client.</param>
    /// <param name="descriptor">The tool descriptor from <c>tools/list</c>.</param>
    public McpTool(IMcpClient client, McpToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(descriptor);

        _client = client;
        Spec = new ToolSpec(descriptor.Name, descriptor.Description, descriptor.InputSchemaJson);
    }

    /// <inheritdoc />
    public ToolSpec Spec { get; }

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var result = await _client.CallToolAsync(Spec.Name, argumentsJson, ct).ConfigureAwait(false);
            return new ToolResult(result.Ok, result.Content);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ITool contract: a failure is a result; it must not throw and abort the turn.
            return new ToolResult(false, ex.Message);
        }
    }
}
