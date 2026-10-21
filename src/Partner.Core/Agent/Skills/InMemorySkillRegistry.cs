namespace Partner.Core.Agent.Skills;

/// <summary>
/// In-memory <see cref="ISkillRegistry"/>.
/// <para>
/// <see cref="Reload"/> swaps in a whole new snapshot, so a reader (the injection hook, on the turn loop) sees
/// either the old set or the new one, never a half-built mix; there is no locking. Ids are compared ordinally
/// (case-sensitive).
/// </para>
/// <para>
/// On duplicate ids, <see cref="Reload"/> keeps the first occurrence and ignores later ones;
/// <see cref="SkillDefinition.SkillId"/> comes from a unique directory name, so normal loading produces none.
/// </para>
/// </summary>
public sealed class InMemorySkillRegistry : ISkillRegistry
{
    private static readonly IReadOnlyList<SkillDefinition> NoSkills = Array.Empty<SkillDefinition>();

    private sealed record Snapshot(Dictionary<string, SkillDefinition> ById, IReadOnlyList<SkillDefinition> Skills);

    private volatile Snapshot _snapshot = new(new Dictionary<string, SkillDefinition>(StringComparer.Ordinal), NoSkills);

    /// <summary>Creates an empty registry.</summary>
    public InMemorySkillRegistry()
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> Skills => _snapshot.Skills;

    /// <inheritdoc />
    public SkillDefinition? Find(string skillId) => _snapshot.ById.TryGetValue(skillId, out var skill) ? skill : null;

    /// <inheritdoc />
    public void Reload(IEnumerable<SkillDefinition> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        var byId = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            ArgumentNullException.ThrowIfNull(skill);

            // Duplicate id: keep the first occurrence and ignore later ones.
            byId.TryAdd(skill.SkillId, skill);
        }

        _snapshot = new Snapshot(byId, byId.Values.OrderBy(static skill => skill.SkillId, StringComparer.Ordinal).ToArray());
    }
}
