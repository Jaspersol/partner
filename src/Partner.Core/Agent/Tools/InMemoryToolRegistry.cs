namespace Partner.Core.Agent.Tools;

/// <summary>
/// <see cref="IToolRegistry"/> 的内存实现（设计文档 §2.9.6）。
/// <para>
/// 登记发生在组装期（单线程），运行期只读，因此不加锁。工具名按序号（大小写敏感）比较。
/// </para>
/// </summary>
public sealed class InMemoryToolRegistry : IToolRegistry
{
    private readonly List<ITool> _tools = [];
    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);

    /// <summary>创建一个空注册表。</summary>
    public InMemoryToolRegistry()
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolSpec> Specs => _tools.Select(t => t.Spec).ToArray();

    /// <inheritdoc />
    public ITool? Find(string name) => _byName.TryGetValue(name, out var tool) ? tool : null;

    /// <inheritdoc />
    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var name = tool.Spec.Name;
        if (_byName.ContainsKey(name))
        {
            throw new InvalidOperationException($"工具名重复注册：{name}。");
        }

        _byName.Add(name, tool);
        _tools.Add(tool);
    }
}
