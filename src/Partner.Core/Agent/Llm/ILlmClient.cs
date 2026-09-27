namespace Partner.Core.Agent.Llm;

/// <summary>
/// LLM 客户端抽象（设计文档 §2.9.5）。叶子模块：不含 HTTP、不读写磁盘，
/// 由回合运行器组合；真实实现（HTTP）与脚本实现（测试/诊断）都实现本接口。
/// </summary>
public interface ILlmClient
{
    /// <summary>发起一次补全。实现不得吞掉 <paramref name="ct"/> 的取消。</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// 同一次补全，但把助手文本**边到边报**（<paramref name="onText"/> 收到的是增量片段，不是累计值）。
    /// <para>
    /// 默认实现让接口保持不破坏：没有流式通道的客户端一次报完。有流式的实现必须对非空文本至少回调一次，
    /// 且**仍然返回完整响应**——调用方写日志用的是返回值，不是回调，所以流到一半崩掉不会把半句当成答案
    /// 记进会话。
    /// </para>
    /// </summary>
    async Task<LlmResponse> StreamAsync(LlmRequest request, Action<string> onText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onText);

        var response = await CompleteAsync(request, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response.Text))
        {
            onText(response.Text);
        }

        return response;
    }
}
