namespace Partner.Core.Agent.Tools;

/// <summary>
/// 工具的对外描述（设计文档 §2.9.6）：暴露给模型的名称、说明与 JSON Schema 参数。
/// </summary>
/// <param name="Name">工具名（注册表内的唯一键）。</param>
/// <param name="Description">给模型看的用途说明。</param>
/// <param name="ParametersJsonSchema">参数的 JSON Schema（原始 JSON 文本）。</param>
public sealed record ToolSpec(string Name, string Description, string ParametersJsonSchema);

/// <summary>
/// 一次工具调用的结果（设计文档 §2.9.6）。失败也是结果（<see cref="Ok"/> 为 <see langword="false"/>），
/// 以文本回填给模型，而不是抛异常中断回合。
/// </summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Content">回填给模型的文本（成功为输出，失败为错误说明）。</param>
public sealed record ToolResult(bool Ok, string Content);
