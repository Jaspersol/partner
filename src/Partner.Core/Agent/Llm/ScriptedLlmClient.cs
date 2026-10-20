namespace Partner.Core.Agent.Llm;

/// <summary>
/// A <see cref="ILlmClient"/> stand-in for tests/diagnostics: replays responses in the order given at
/// construction and records every request.
/// <para>
/// <b>Replay</b>: <see cref="CompleteAsync"/> dequeues strictly in order; when the queue is exhausted it throws
/// <see cref="InvalidOperationException"/> — a scripted turn never invents a response, so a missing response is
/// a script error and must be supplied explicitly.
/// </para>
/// <para>
/// <b>Recording</b>: every request is recorded (including the one that failed because the script ran out), with
/// a snapshot of the message list, so a caller mutating its own list afterwards cannot rewrite history.
/// </para>
/// <para>
/// <b>Cancellation</b>: an already-cancelled token throws <see cref="OperationCanceledException"/> <b>before</b>
/// dequeueing, leaving the queue untouched.
/// </para>
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<LlmResponse> _script;
    private readonly List<LlmRequest> _requests = [];

    /// <param name="responses">The response script, replayed in order.</param>
    public ScriptedLlmClient(IEnumerable<LlmResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _script = new Queue<LlmResponse>(responses);
    }

    /// <summary>All requests received, in arrival order, with messages snapshotted.</summary>
    public IReadOnlyList<LlmRequest> Requests => _requests;

    /// <summary>Number of scripted responses not yet replayed.</summary>
    public int Remaining => _script.Count;

    /// <inheritdoc />
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // Record before dequeueing, so an exhausted script still shows which request it did not cover.
        _requests.Add(request with { Messages = request.Messages.ToArray() });

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"ScriptedLlmClient 的脚本响应已耗尽（已记录 {_requests.Count} 次请求）：脚本化回合不会凭空编造响应，请为每个回合补足响应。");
        }

        return Task.FromResult(_script.Dequeue());
    }
}
