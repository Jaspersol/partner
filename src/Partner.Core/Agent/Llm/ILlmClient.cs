namespace Partner.Core.Agent.Llm;

/// <summary>
/// The LLM client abstraction. A leaf module: no HTTP, no disk, composed by the turn runner. Both the real
/// HTTP implementation and the scripted one (tests/diagnostics) implement it.
/// </summary>
public interface ILlmClient
{
    /// <summary>Runs one completion. Implementations must not swallow cancellation of <paramref name="ct"/>.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// The same completion, reporting assistant text <b>as it arrives</b> (<paramref name="onText"/> receives
    /// incremental fragments, not the accumulated value).
    /// <para>
    /// A default implementation keeps the interface non-breaking: a client without a streaming channel reports
    /// everything at once. A streaming implementation must call back at least once for non-empty text, and
    /// <b>still return the full response</b> — the caller logs the return value, not the callbacks, so a stream
    /// that dies halfway never records half an answer.
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

    /// <summary>
    /// The same completion, reporting text <b>and</b> reasoning as they arrive.
    /// <para>
    /// A default method again, so a client that only knows <see cref="CompleteAsync"/> still satisfies the
    /// interface unchanged — and still gets its reasoning through <see cref="LlmResponse.Reasoning"/>, just
    /// not while it happens. The interface's contract holds on this path too: non-empty text is reported at
    /// least once, and the <b>return value</b> is what the caller records, so a stream that dies halfway leaves a
    /// turn without an answer rather than half an answer a later turn would read as complete.
    /// </para>
    /// </summary>
    async Task<LlmResponse> StreamWithReasoningAsync(
        LlmRequest request,
        Action<string> onText,
        Action<string> onReasoning,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onText);
        ArgumentNullException.ThrowIfNull(onReasoning);

        var response = await StreamAsync(request, onText, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response.Reasoning))
        {
            onReasoning(response.Reasoning);
        }

        return response;
    }
}
