namespace Partner.Core.Agent.Skills;

/// <summary>
/// One workspace skill: the parsed form of <c>&lt;workspace&gt;/skills/&lt;skillId&gt;/SKILL.md</c>.
/// </summary>
public sealed record SkillDefinition
{
    /// <summary>Skill id: the skill directory name, all lowercase (matches <c>^[a-z0-9][a-z0-9._-]*$</c>).</summary>
    public required string SkillId { get; init; }

    /// <summary>Display name, from the front-matter <c>name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Single-line description, from the front-matter <c>description</c>; the loader truncates it when too long.</summary>
    public required string Description { get; init; }

    /// <summary>Absolute path of the skill directory.</summary>
    public required string Directory { get; init; }

    /// <summary>The body after the front matter, kept verbatim (at most one leading newline is stripped).</summary>
    public required string Instructions { get; init; }
}
