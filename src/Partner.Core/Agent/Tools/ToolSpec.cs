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
/// <summary>
/// What a tool hands back.
/// </summary>
/// <param name="Ok">Whether the tool did what it was asked.</param>
/// <param name="Content">The text the model reads (and that the log records).</param>
/// <param name="Images">
/// Pictures to show the model, as data URIs. A tool result is text in the log, so the bytes travel beside it:
/// the log keeps the id, the request carries the pixels. Only ever set when the configured model takes images.
/// </param>
public sealed record ToolResult(bool Ok, string Content, IReadOnlyList<string>? Images = null);
