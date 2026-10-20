namespace Partner.Core.Agent.Tools;

/// <summary>
/// 一个可被模型调用的工具（设计文档 §2.9.6）。
/// <para>
/// 实现只负责执行：参数解析与失败都通过 <see cref="ToolResult"/> 返回，不抛出以中断回合。
/// 是否允许调用由 <see cref="IToolGovernance"/> 在调用<b>之前</b>决定。
/// </para>
/// </summary>
public interface ITool
{
    /// <summary>工具描述（注册表与模型都以此为准）。</summary>
    ToolSpec Spec { get; }

    /// <param name="argumentsJson">模型给出的原始参数 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// A tool that knows how long it legitimately needs. The runner's per-call timeout is one number for every
/// tool; a tool with its own time budget (a search that is allowed to wait for slow engines) would otherwise be
/// cancelled by that number and lose the results it had already collected.
/// </summary>
public interface ITimedTool : ITool
{
    /// <summary>The longest this call may run. Only ever extends the runner's default, never shortens it.</summary>
    TimeSpan Timeout { get; }
}
