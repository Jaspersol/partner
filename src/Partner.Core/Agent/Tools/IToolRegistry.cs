namespace Partner.Core.Agent.Tools;

/// <summary>
/// 工具注册表（设计文档 §2.9.6）：组装期登记，运行期只读查找。
/// </summary>
public interface IToolRegistry
{
    /// <summary>已注册工具的规格，按注册顺序（决定暴露给模型的顺序）。</summary>
    IReadOnlyList<ToolSpec> Specs { get; }

    /// <summary>按名查找；未注册返回 <see langword="null"/>（不抛异常）。</summary>
    ITool? Find(string name);

    /// <summary>登记一个工具；重名抛 <see cref="InvalidOperationException"/>。</summary>
    void Register(ITool tool);
}
