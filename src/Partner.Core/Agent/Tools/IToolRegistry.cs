namespace Partner.Core.Agent.Tools;

/// <summary>
/// Tool registry: registered at assembly time, read-only lookups at run time.
/// </summary>
public interface IToolRegistry
{
    /// <summary>Specs of the registered tools, in registration order (the order exposed to the model).</summary>
    IReadOnlyList<ToolSpec> Specs { get; }

    /// <summary>Finds a tool by name; returns <see langword="null"/> when not registered (never throws).</summary>
    ITool? Find(string name);

    /// <summary>Registers a tool; a duplicate name throws <see cref="InvalidOperationException"/>.</summary>
    void Register(ITool tool);
}
