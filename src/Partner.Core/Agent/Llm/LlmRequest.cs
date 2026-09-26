using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Llm;

/// <summary>
/// 一次补全请求（设计文档 §2.9.5）：完整对话历史 + 本轮可见的工具清单。
/// </summary>
/// <param name="Messages">对话历史，按时间顺序。</param>
/// <param name="Tools">本轮暴露给模型的工具（已由治理层筛过）。</param>
/// <param name="MaxTokens">可选的输出上限。</param>
/// <param name="Model">可选的模型覆盖。</param>
public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<ToolSpec> Tools,
    int? MaxTokens = null,
    string? Model = null);
