namespace Partner.Core.Agent.Skills;

/// <summary>
/// 技能注册表（设计文档缺口 C）：持有当前工作区已加载的技能，供注入钩子读取。
/// </summary>
public interface ISkillRegistry
{
    /// <summary>当前技能，按 <see cref="SkillDefinition.SkillId"/> 升序（<see cref="StringComparer.Ordinal"/>）排列。</summary>
    IReadOnlyList<SkillDefinition> Skills { get; }

    /// <summary>
    /// 按标识查找；未命中返回 <see langword="null"/>。
    /// 标识按序号比较（大小写敏感），因此 <c>alpha</c> 与 <c>Alpha</c> 是两个不同的技能。
    /// </summary>
    SkillDefinition? Find(string skillId);

    /// <summary>整体替换当前技能集合（加载器的输出）。</summary>
    void Reload(IEnumerable<SkillDefinition> skills);
}
