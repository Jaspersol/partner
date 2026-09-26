using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Mcp;

/// <summary>Exposes one MCP tool through the local ITool registry, so governance applies to it too.</summary>
public sealed class McpTool : ITool
{
    private readonly IMcpClient _client;

    /// <param name="client">已启动的 MCP 客户端。</param>
    /// <param name="descriptor">来自 <c>tools/list</c> 的工具描述。</param>
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
            // ITool 契约：失败也是结果，不抛异常中断回合。
            return new ToolResult(false, ex.Message);
        }
    }
}
