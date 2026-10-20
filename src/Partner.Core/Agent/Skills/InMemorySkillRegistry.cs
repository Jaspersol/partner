namespace Partner.Core.Agent.Skills;

/// <summary>
/// In-memory <see cref="ISkillRegistry"/>.
/// <para>
/// Loading happens at assembly time (single-threaded) and the registry is read-only afterwards, so there is
/// no locking. Ids are compared ordinally (case-sensitive).
/// </para>
/// <para>
/// On duplicate ids, <see cref="Reload"/> keeps the first occurrence and ignores later ones;
/// <see cref="SkillDefinition.SkillId"/> comes from a unique directory name, so normal loading produces none.
/// </para>
/// </summary>
public sealed class InMemorySkillRegistry : ISkillRegistry
{
    private static readonly IReadOnlyList<SkillDefinition> NoSkills = Array.Empty<SkillDefinition>();

    private readonly Dictionary<string, SkillDefinition> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<SkillDefinition> _skills = NoSkills;

    /// <summary>Creates an empty registry.</summary>
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

            // Duplicate id: keep the first occurrence and ignore later ones.
            _byId.TryAdd(skill.SkillId, skill);
        }

        _skills = _byId.Values.OrderBy(static skill => skill.SkillId, StringComparer.Ordinal).ToArray();
    }
}
