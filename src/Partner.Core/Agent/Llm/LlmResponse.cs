namespace Partner.Core.Agent.Llm;

/// <summary>
/// The result of one completion.
/// </summary>
/// <param name="Text">Assistant text; may be <see langword="null"/> for a tool-call-only turn.</param>
/// <param name="ToolCalls">Tool calls requested by the model; an empty list when none (never <see langword="null"/>).</param>
/// <param name="FinishReason">Finish reason: <c>"stop"</c> / <c>"tool_calls"</c> / <c>"length"</c>.</param>
/// <param name="PromptTokens">Prompt token count (0 when unknown).</param>
/// <param name="CompletionTokens">Completion token count (0 when unknown).</param>
/// <param name="Reasoning">
/// The model's reasoning. <see langword="null"/> when absent, not an empty string: "the model did not reason"
/// and "the endpoint sent none" are the same answer, and the UI should not draw them differently.
/// </param>
public sealed record LlmResponse(
    string? Text,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string FinishReason,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    string? Reasoning = null);
