namespace Partner.Core.Agent.Tools;

/// <summary>
/// 人工批准通道（设计文档 §2.9.6）：治理结论为 <see cref="ToolDecision.Ask"/> 时，由回合运行器调用。
/// </summary>
public interface IApprovalHandler
{
    /// <param name="toolName">待批准的工具名。</param>
    /// <param name="argumentsJson">模型给出的原始参数 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>批准为 <see langword="true"/>。</returns>
    Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// 无头环境的默认批准通道：从不批准（设计文档 §2.9.6）。无副作用、不阻塞。
/// </summary>
public sealed class DenyAllApprovalHandler : IApprovalHandler
{
    /// <summary>创建一个始终拒绝的批准通道。</summary>
    public DenyAllApprovalHandler()
    {
    }

    /// <inheritdoc />
    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        => Task.FromResult(false);
}
