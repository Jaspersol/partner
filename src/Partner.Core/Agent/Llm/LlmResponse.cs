namespace Partner.Core.Agent.Llm;

/// <summary>
/// 一次补全的结果（设计文档 §2.9.5）。
/// </summary>
/// <param name="Text">助手文本；纯工具调用回合可以为 <see langword="null"/>。</param>
/// <param name="ToolCalls">模型请求的工具调用；无则为空列表（非 <see langword="null"/>）。</param>
/// <param name="FinishReason">结束原因：<c>"stop"</c> / <c>"tool_calls"</c> / <c>"length"</c>。</param>
/// <param name="PromptTokens">提示词 token 数（未知为 0）。</param>
/// <param name="CompletionTokens">补全 token 数（未知为 0）。</param>
/// <param name="Reasoning">
/// 模型的思考内容。没有就是 <see langword="null"/>，不是空串——「模型没思考」与「端点压根没给」是同一个
/// 答案，界面也不该把它们画成两回事。
/// </param>
public sealed record LlmResponse(
    string? Text,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string FinishReason,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    string? Reasoning = null);
