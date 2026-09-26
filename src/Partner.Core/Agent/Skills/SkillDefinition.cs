namespace Partner.Core.Agent.Skills;

/// <summary>
/// 一个工作区技能（设计文档缺口 C）：<c>&lt;workspace&gt;/skills/&lt;skillId&gt;/SKILL.md</c> 的解析结果。
/// </summary>
public sealed record SkillDefinition
{
    /// <summary>技能标识：技能目录名，全小写（等价于 <c>^[a-z0-9][a-z0-9._-]*$</c>）。</summary>
    public required string SkillId { get; init; }

    /// <summary>展示名：取自前置元数据的 <c>name</c>。</summary>
    public required string Name { get; init; }

    /// <summary>单行描述：取自前置元数据的 <c>description</c>；超长时由加载器截断。</summary>
    public required string Description { get; init; }

    /// <summary>技能目录的绝对路径。</summary>
    public required string Directory { get; init; }

    /// <summary>前置元数据之后的正文，原样保留（仅至多剥离一个前导换行）。</summary>
    public required string Instructions { get; init; }
}
