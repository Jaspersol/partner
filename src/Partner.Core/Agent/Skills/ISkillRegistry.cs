namespace Partner.Core.Agent.Skills;

/// <summary>
/// The skill registry: holds the skills loaded from the current workspace for the injection hook to read.
/// </summary>
public interface ISkillRegistry
{
    /// <summary>The current skills, ordered by <see cref="SkillDefinition.SkillId"/> ascending (<see cref="StringComparer.Ordinal"/>).</summary>
    IReadOnlyList<SkillDefinition> Skills { get; }

    /// <summary>
    /// Finds a skill by id; returns <see langword="null"/> when absent. Ids are compared ordinally
    /// (case-sensitive), so <c>alpha</c> and <c>Alpha</c> are different skills.
    /// </summary>
    SkillDefinition? Find(string skillId);

    /// <summary>Replaces the whole skill set (the loader's output).</summary>
    void Reload(IEnumerable<SkillDefinition> skills);
}
