namespace Partner.Core.Agent.Llm;

/// <summary>
/// One tool call requested by the model. <see cref="ArgumentsJson"/> is the raw JSON string the model produced;
/// this layer neither parses nor validates it — the tool itself and the governance layer do.
/// </summary>
/// <param name="Id">The call id given by the model; used when filling in a <c>role = "tool"</c> message.</param>
/// <param name="Name">Tool name.</param>
/// <param name="ArgumentsJson">Tool arguments (raw JSON text).</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One conversation message. Immutable value object.
/// </summary>
/// <param name="Role">Role: <c>"system"</c> / <c>"user"</c> / <c>"assistant"</c> / <c>"tool"</c>.</param>
/// <param name="Content">Text content; may be <see langword="null"/> for a tool-call-only message.</param>
/// <param name="ToolCalls">Tool calls carried by an assistant message; <see langword="null"/> when none.</param>
/// <param name="ToolCallId">Set only when <c>role == "tool"</c>: the matching <see cref="LlmToolCall.Id"/>.</param>
/// <param name="Name">Optional name (for example the tool name when <c>role == "tool"</c>).</param>
/// <param name="Images">Data URIs of attached images, in attach order; <see langword="null"/> when none.</param>
public sealed record LlmMessage(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<string>? Images = null);
