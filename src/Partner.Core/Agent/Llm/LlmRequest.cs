using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Llm;

/// <summary>
/// 一次补全请求（设计文档 §2.9.5）：完整对话历史 + 本轮可见的工具清单。
/// </summary>
/// <param name="Messages">对话历史，按时间顺序。</param>
/// <param name="Tools">本轮暴露给模型的工具（已由治理层筛过）。</param>
/// <param name="MaxTokens">可选的输出上限。</param>
/// <param name="Model">可选的模型覆盖。</param>
/// <param name="ReasoningEffort">
/// 可选的思考挡位覆盖。
/// <para>
/// null（缺省）= 用客户端配置里的默认挡位；显式给值 = 这一轮用这个，包括 <c>off</c>。放在请求上而不是
/// 重建客户端，是因为挡位是**逐轮**的选择：改挡位要能只影响下一轮，而重建客户端得停掉消费者，
/// 一次改一个值不该打断正在跑的回合。
/// </para>
/// </param>
public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<ToolSpec> Tools,
    int? MaxTokens = null,
    string? Model = null,
    string? ReasoningEffort = null);
