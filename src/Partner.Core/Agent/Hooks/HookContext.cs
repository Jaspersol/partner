using Partner.Core.Contracts;

namespace Partner.Core.Agent.Hooks;

/// <summary>
/// 钩子运行时上下文（设计文档 §2.9.3）：一轮一个实例，随阶段在钩子之间传递。
/// <para>
/// 非线程安全：由同一轮的执行线程独占使用；<see cref="InjectedPrompts"/> 的排序缓存在首次读取时建立。
/// </para>
/// </summary>
public sealed class HookContext
{
    private readonly List<(int Priority, string Text, string? Label)> _injected = new();
    private IReadOnlyList<string>? _orderedInjected;

    /// <summary>本轮所属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>触发本轮的输入来源。</summary>
    public required InputSource Source { get; init; }

    /// <summary>原始输入载荷（语音转写文本、用户输入文本或调度事件载荷）。</summary>
    public required string InputPayload { get; init; }

    /// <summary>本轮工作目录。</summary>
    public required string WorkspaceDirectory { get; init; }

    /// <summary>钩子注入的提示词，按优先级（升序）再按插入顺序排列。</summary>
    public IReadOnlyList<string> InjectedPrompts =>
        // LINQ 的 OrderBy 是稳定排序：同优先级保持插入顺序。
        _orderedInjected ??= _injected.OrderBy(e => e.Priority).Select(e => e.Text).ToArray();

    /// <summary>
    /// 与 <see cref="InjectedPrompts"/> 一一对应的标签，没有标签的位置为 null。
    /// <para>
    /// 单独一条通道，不拼进正文：标签是给人读的，正文是给模型读的，两者混在一起会让模型看到本来只属于
    /// 界面的词。
    /// </para>
    /// </summary>
    public IReadOnlyList<string?> InjectedLabels =>
        _injected.OrderBy(e => e.Priority).Select(e => e.Label).ToArray();

    /// <summary>钩子对之间传递数据的命名空间字典（快照等）。</summary>
    public IDictionary<string, string> Extras { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>错误信息；由 <see cref="AgentPhase.OnError"/> 阶段的钩子写入。</summary>
    public string? Error { get; set; }

    /// <summary>
    /// 注入一段提示词。<paramref name="priority"/> 越小越靠前；相同优先级保持插入顺序。
    /// </summary>
    /// <param name="label">
    /// 这段文字是什么，给读会话的人看（例如 <c>recall</c>）。只用于展示：提示词本身不变，模型看到的仍是
    /// <paramref name="text"/>。不填时会话页把它当作普通系统行。
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="text"/> 为 null、空或纯空白。</exception>
    public void Inject(string text, int priority = 100, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _injected.Add((priority, text, label));
        _orderedInjected = null;
    }
}
