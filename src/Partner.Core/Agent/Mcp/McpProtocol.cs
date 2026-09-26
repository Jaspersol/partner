namespace Partner.Core.Agent.Mcp;

/// <summary>
/// 一个通过 stdio 启动的 MCP 服务器配置（设计文档 §2.6.3）。
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>服务器名（日志与错误信息中使用，同时是注册表内的命名空间）。</summary>
    public required string Name { get; init; }

    /// <summary>可执行文件名或路径，例如 <c>dotnet</c>。</summary>
    public required string Command { get; init; }

    /// <summary>命令行参数（逐个传递，不经过 shell 解析）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>子进程工作目录；<see langword="null"/> 表示继承当前进程。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>追加/覆盖到子进程环境变量上的键值对。</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>单次请求（initialize / tools/list / tools/call）的超时。</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a tools/list result stays valid (design doc: 10s).</summary>
    public TimeSpan CapabilityCacheTtl { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>MCP 服务器 <c>tools/list</c> 返回的单个工具描述。</summary>
/// <param name="Name">工具名。</param>
/// <param name="Description">给模型看的用途说明。</param>
/// <param name="InputSchemaJson">参数的 JSON Schema（原始 JSON 文本）。</param>
public sealed record McpToolDescriptor(string Name, string Description, string InputSchemaJson);

/// <summary>一次 MCP <c>tools/call</c> 的结果。</summary>
/// <param name="Ok">是否成功（协议错误或 <c>isError</c> 都为 <see langword="false"/>）。</param>
/// <param name="Content">拼接后的文本内容（失败时为错误说明）。</param>
/// <param name="IsError">是否由工具/协议显式报告为错误。</param>
public sealed record McpCallResult(bool Ok, string Content, bool IsError);
