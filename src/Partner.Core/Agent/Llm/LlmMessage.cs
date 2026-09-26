namespace Partner.Core.Agent.Llm;

/// <summary>
/// 模型请求的一次工具调用（设计文档 §2.9.5）。<see cref="ArgumentsJson"/> 是模型给出的原始 JSON 字符串，
/// 本层不解析、不校验——由工具自身与治理层负责。
/// </summary>
/// <param name="Id">模型给出的调用标识，回填 <c>role = "tool"</c> 的消息时使用。</param>
/// <param name="Name">工具名。</param>
/// <param name="ArgumentsJson">工具参数（原始 JSON 文本）。</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// 一次对话消息（设计文档 §2.9.5）。不可变值对象。
/// </summary>
/// <param name="Role">角色：<c>"system"</c> / <c>"user"</c> / <c>"assistant"</c> / <c>"tool"</c>。</param>
/// <param name="Content">文本内容；纯工具调用消息可以为 <see langword="null"/>。</param>
/// <param name="ToolCalls">助手消息携带的工具调用；无则为 <see langword="null"/>。</param>
/// <param name="ToolCallId">仅 <c>role == "tool"</c> 时设置：对应 <see cref="LlmToolCall.Id"/>。</param>
/// <param name="Name">可选名字（例如 <c>role == "tool"</c> 时的工具名）。</param>
public sealed record LlmMessage(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);
