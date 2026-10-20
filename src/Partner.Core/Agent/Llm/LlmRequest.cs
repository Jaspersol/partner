using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Llm;

/// <summary>
/// One completion request: the full conversation history plus the tool list visible to this turn.
/// </summary>
/// <param name="Messages">Conversation history, in chronological order.</param>
/// <param name="Tools">Tools exposed to the model this turn (already filtered by the governance layer).</param>
/// <param name="MaxTokens">Optional output cap.</param>
/// <param name="Model">Optional model override.</param>
/// <param name="ReasoningEffort">
/// Optional thinking-gear override.
/// <para>
/// null (default) = use the client configuration's default gear; an explicit value = use it for this turn,
/// including <c>off</c>. It lives on the request rather than rebuilding the client because the gear is a
/// <b>per-turn</b> choice: changing it must affect only the next turn, while rebuilding the client would stop
/// the consumer.
/// </para>
/// </param>
public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<ToolSpec> Tools,
    int? MaxTokens = null,
    string? Model = null,
    string? ReasoningEffort = null);
