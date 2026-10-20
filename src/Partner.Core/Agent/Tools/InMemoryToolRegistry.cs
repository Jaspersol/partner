namespace Partner.Core.Agent.Tools;

/// <summary>
/// In-memory <see cref="IToolRegistry"/>.
/// <para>
/// Registration happens at assembly time (single-threaded) and the registry is read-only afterwards, so there
/// is no locking. Tool names are compared ordinally (case-sensitive).
/// </para>
/// </summary>
public sealed class InMemoryToolRegistry : IToolRegistry
{
    private readonly List<ITool> _tools = [];
    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);

    /// <summary>Creates an empty registry.</summary>
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
