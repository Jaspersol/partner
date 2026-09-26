namespace Partner.Core.Agent.Mcp;

/// <summary>
/// 一个 MCP 服务器连接（设计文档 §2.6.3）：生命周期、能力发现与工具调用。
/// <para>
/// 实现必须保证：进程死亡或未启动时调用立即抛 <see cref="InvalidOperationException"/>（不挂起），
/// 单次请求超过 <see cref="McpServerConfig.CallTimeout"/> 时抛 <see cref="TimeoutException"/>。
/// </para>
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>子进程是否存活（已停止或已退出为 <see langword="false"/>）。</summary>
    bool IsRunning { get; }

    /// <summary>配置中的服务器名。</summary>
    string ServerName { get; }

    /// <summary>启动子进程并完成 <c>initialize</c> 握手与 <c>notifications/initialized</c> 通知。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 列出服务器暴露的工具；在 <see cref="McpServerConfig.CapabilityCacheTtl"/> 内重复调用返回缓存，
    /// 不产生新的 <c>tools/list</c> 请求。
    /// </summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>调用一个工具；协议错误与 <c>isError</c> 都通过 <see cref="McpCallResult"/> 返回。</summary>
    Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default);

    /// <summary>关闭 stdin、等待子进程退出（最多 2 秒）后杀掉进程树；可重复调用。</summary>
    Task StopAsync(CancellationToken ct = default);
}
