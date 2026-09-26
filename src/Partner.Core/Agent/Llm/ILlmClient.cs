namespace Partner.Core.Agent.Llm;

/// <summary>
/// LLM 客户端抽象（设计文档 §2.9.5）。叶子模块：不含 HTTP、不读写磁盘，
/// 由回合运行器组合；真实实现（HTTP）与脚本实现（测试/诊断）都实现本接口。
/// </summary>
public interface ILlmClient
{
    /// <summary>发起一次补全。实现不得吞掉 <paramref name="ct"/> 的取消。</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}
