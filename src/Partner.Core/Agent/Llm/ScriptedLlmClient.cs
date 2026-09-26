namespace Partner.Core.Agent.Llm;

/// <summary>
/// 测试/诊断用的 <see cref="ILlmClient"/> 替身（设计文档 §2.9.5）：按构造时给定的顺序回放响应，并记录每一次请求。
/// <para>
/// <b>回放</b>：<see cref="CompleteAsync"/> 严格按队列顺序出队；队列耗尽时抛 <see cref="InvalidOperationException"/>——
/// 脚本化回合不会凭空编造响应，缺响应即脚本错误，必须显式补足。
/// </para>
/// <para>
/// <b>记录</b>：每个请求都会被记录（包括因脚本耗尽而失败的那一次），并对消息列表做快照，
/// 因此调用方事后修改自己传入的列表不会改写历史。
/// </para>
/// <para>
/// <b>取消</b>：已取消的令牌在出队<b>之前</b>抛出 <see cref="OperationCanceledException"/>，队列保持原样。
/// </para>
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<LlmResponse> _script;
    private readonly List<LlmRequest> _requests = [];

    /// <param name="responses">按顺序回放的响应脚本。</param>
    public ScriptedLlmClient(IEnumerable<LlmResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _script = new Queue<LlmResponse>(responses);
    }

    /// <summary>已收到的全部请求（按到达顺序，消息已快照）。</summary>
    public IReadOnlyList<LlmRequest> Requests => _requests;

    /// <summary>脚本中尚未回放的响应数量。</summary>
    public int Remaining => _script.Count;

    /// <inheritdoc />
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // 先记录再出队：脚本耗尽时也能看到「是哪个请求没有被脚本覆盖」。
        _requests.Add(request with { Messages = request.Messages.ToArray() });

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"ScriptedLlmClient 的脚本响应已耗尽（已记录 {_requests.Count} 次请求）：脚本化回合不会凭空编造响应，请为每个回合补足响应。");
        }

        return Task.FromResult(_script.Dequeue());
    }
}
