namespace Partner.Core.Agent.Skills;

/// <summary>
/// <see cref="ISkillRegistry"/> 的内存实现（设计文档缺口 C）。
/// <para>
/// 加载发生在组装期（单线程），运行期只读，因此不加锁。标识按序号（大小写敏感）比较。
/// </para>
/// <para>
/// <see cref="Reload"/> 遇到重复标识时<b>保留先出现的一个</b>，其后同名项被忽略；
/// 由于 <see cref="SkillDefinition.SkillId"/> 来自唯一的目录名，正常加载不会产生重复。
/// </para>
/// </summary>
public sealed class InMemorySkillRegistry : ISkillRegistry
{
    private static readonly IReadOnlyList<SkillDefinition> NoSkills = Array.Empty<SkillDefinition>();

    private readonly Dictionary<string, SkillDefinition> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<SkillDefinition> _skills = NoSkills;

    /// <summary>创建一个空注册表。</summary>
    public InMemorySkillRegistry()
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> Skills => _skills;

    /// <inheritdoc />
    public SkillDefinition? Find(string skillId) => _byId.TryGetValue(skillId, out var skill) ? skill : null;

    /// <inheritdoc />
    public void Reload(IEnumerable<SkillDefinition> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        _byId.Clear();
        foreach (var skill in skills)
        {
            ArgumentNullException.ThrowIfNull(skill);

            // 重复标识：保留先出现的一个，忽略后续同名项。
            _byId.TryAdd(skill.SkillId, skill);
        }

        _skills = _byId.Values.OrderBy(static skill => skill.SkillId, StringComparer.Ordinal).ToArray();
    }
}
