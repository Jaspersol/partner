namespace Partner.Core.Agent.Skills;

/// <summary>一次工作区技能扫描的结果。</summary>
/// <param name="Skills">已应用数量上限、按 <see cref="SkillDefinition.SkillId"/> 升序排列的技能。</param>
/// <param name="Warnings">
/// 跳过或丢弃的原因说明。顺序确定：逐路径的警告按路径序号（<see cref="StringComparer.Ordinal"/>）排列，
/// 数量上限警告追加在最后。
/// </param>
public sealed record SkillLoadResult(IReadOnlyList<SkillDefinition> Skills, IReadOnlyList<string> Warnings);

/// <summary>
/// 工作区技能扫描器（设计文档缺口 C：Skills 的注入格式与工作区扫描规则）。
/// <para>
/// 只识别 <c>&lt;workspaceDirectory&gt;/skills/&lt;skillId&gt;/SKILL.md</c> 这一种形态：
/// <c>skills</c> 目录不存在时返回空结果且<b>不产生警告</b>；
/// 目录名不匹配 <c>^[a-z0-9][a-z0-9._-]*$</c>、缺少 <c>SKILL.md</c>、
/// 前置元数据缺失或未闭合、必填字段缺失或为空时，记一条警告并跳过该技能。
/// </para>
/// <para>
/// <b>文件格式</b>：首行必须恰为 <c>---</c>，前置元数据在下一个恰为 <c>---</c> 的行结束。
/// 键为 <c>name</c> 与 <c>description</c>（键名大小写不敏感，值去除首尾空白），两者必填；
/// 未知键、空行以及不含 <c>:</c> 的行一律静默忽略（保持宽容）。
/// <c>description</c> 由构造保证单行；超过 <c>maxDescriptionChars</c> 时截断并追加 <c>…</c>。
/// <see cref="SkillDefinition.Instructions"/> 为结束分隔线之后的全部内容，至多剥离一个前导换行。
/// </para>
/// <para>内容不合法绝不抛异常，一律降级为警告；文件本身的 I/O 失败则照常抛出。</para>
/// </summary>
public sealed class SkillLoader
{
    private const string SkillsDirectoryName = "skills";
    private const string SkillFileName = "SKILL.md";
    private const string FrontMatterDelimiter = "---";
    private const string Ellipsis = "…";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";

    private readonly int _maxSkills;
    private readonly int _maxDescriptionChars;

    /// <summary>创建扫描器。</summary>
    /// <param name="maxSkills">最多返回的技能数；多余的按 <see cref="SkillDefinition.SkillId"/> 升序丢弃并记一条警告。</param>
    /// <param name="maxDescriptionChars">描述的最大字符数，超出部分截断并追加 <c>…</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxSkills"/> 为负，或 <paramref name="maxDescriptionChars"/> 小于 1。
    /// </exception>
    public SkillLoader(int maxSkills = 50, int maxDescriptionChars = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSkills);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDescriptionChars, 1);

        _maxSkills = maxSkills;
        _maxDescriptionChars = maxDescriptionChars;
    }

    /// <summary>扫描 <c>&lt;workspaceDirectory&gt;/skills/*/SKILL.md</c>。内容不合法时不抛异常。</summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceDirectory"/> 为 null、空或纯空白。</exception>
    public SkillLoadResult Load(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);

        // 结果里的 Directory 必须是绝对路径，因此先规范化根目录。
        var skillsDirectory = Path.GetFullPath(Path.Combine(workspaceDirectory, SkillsDirectoryName));
        if (!Directory.Exists(skillsDirectory))
        {
            // 没有 skills 目录不是错误，也不产生警告。
            return new SkillLoadResult(Array.Empty<SkillDefinition>(), Array.Empty<string>());
        }

        var warnings = new List<string>();
        var skills = new List<SkillDefinition>();

        // 目录名即 SkillId；先按路径序号排序，保证警告顺序确定（枚举顺序取决于文件系统）。
        var directories = Directory
            .EnumerateDirectories(skillsDirectory)
            .OrderBy(static directory => directory, StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            var skillId = Path.GetFileName(directory);
            if (!IsValidSkillId(skillId))
            {
                warnings.Add($"{directory}：技能目录名非法，须匹配 ^[a-z0-9][a-z0-9._-]*$（全小写）。");
                continue;
            }

            var skillFile = Path.Combine(directory, SkillFileName);
            if (!File.Exists(skillFile))
            {
                warnings.Add($"{directory}：技能目录缺少 {SkillFileName}。");
                continue;
            }

            var skill = Parse(skillId, directory, skillFile, warnings);
            if (skill is not null)
            {
                skills.Add(skill);
            }
        }

        var ordered = skills.OrderBy(static skill => skill.SkillId, StringComparer.Ordinal).ToArray();
        if (ordered.Length > _maxSkills)
        {
            var dropped = ordered.Skip(_maxSkills).Select(static skill => skill.SkillId);
            warnings.Add($"技能数量超过上限 {_maxSkills}，已丢弃：{string.Join(", ", dropped)}。");
            ordered = ordered[.._maxSkills];
        }

        return new SkillLoadResult(ordered, warnings);
    }

    /// <summary>
    /// 解析单个 <c>SKILL.md</c>；内容不合法时记一条警告并返回 <see langword="null"/>。
    /// </summary>
    private SkillDefinition? Parse(string skillId, string directory, string skillFile, List<string> warnings)
    {
        var text = File.ReadAllText(skillFile);

        // 容忍 UTF-8 BOM：否则首行不会是恰好 ---。
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var lines = SplitLines(text);
        if (lines.Count == 0 || !IsDelimiter(lines[0]))
        {
            warnings.Add($"{skillFile}：缺少前置元数据，首行必须是恰好 ---。");
            return null;
        }

        var closingIndex = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (IsDelimiter(lines[i]))
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
        {
            warnings.Add($"{skillFile}：前置元数据未闭合，缺少恰好 --- 的结束行。");
            return null;
        }

        string? name = null;
        string? description = null;
        for (var i = 1; i < closingIndex; i++)
        {
            // 空行与不含 ':' 的行静默忽略（保持宽容）。
            var line = lines[i].Text.Trim();
            var separator = line.IndexOf(':');
            if (line.Length == 0 || separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // 键名大小写不敏感；未知键忽略。重复键以后者为准。
            if (key.Equals(NameKey, StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals(DescriptionKey, StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            warnings.Add($"{skillFile}：前置元数据缺少必填字段 {NameKey}。");
            return null;
        }

        if (string.IsNullOrEmpty(description))
        {
            warnings.Add($"{skillFile}：前置元数据缺少必填字段 {DescriptionKey}。");
            return null;
        }

        if (description.Length > _maxDescriptionChars)
        {
            description = description[.._maxDescriptionChars] + Ellipsis;
        }

        return new SkillDefinition
        {
            SkillId = skillId,
            Name = name,
            Description = description,
            Directory = directory,
            Instructions = ExtractInstructions(text, lines[closingIndex]),
        };
    }

    /// <summary>取结束分隔线之后的全部内容，至多剥离一个前导换行（即结束行的行终止符）。</summary>
    private static string ExtractInstructions(string text, SourceLine closingLine)
    {
        var start = closingLine.Start + FrontMatterDelimiter.Length;

        if (start < text.Length && text[start] == '\r')
        {
            start++;
        }

        if (start < text.Length && text[start] == '\n')
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>按行切分，记录每行去掉行终止符后的内容与行首下标（兼容 <c>\n</c> 与 <c>\r\n</c>）。</summary>
    private static List<SourceLine> SplitLines(string text)
    {
        var lines = new List<SourceLine>();
        var start = 0;

        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;

            lines.Add(new SourceLine(text[start..contentEnd], start));

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return lines;
    }

    /// <summary>该行是否恰为前置元数据分隔线（允许行尾的 <c>\r</c>）。</summary>
    private static bool IsDelimiter(SourceLine line) =>
        line.Text.AsSpan().SequenceEqual(FrontMatterDelimiter);

    /// <summary>技能标识校验：等价于 <c>^[a-z0-9][a-z0-9._-]*$</c>。</summary>
    private static bool IsValidSkillId(string skillId)
    {
        if (skillId.Length == 0 || !IsLowerAlphaNumeric(skillId[0]))
        {
            return false;
        }

        for (var i = 1; i < skillId.Length; i++)
        {
            var c = skillId[i];
            if (!IsLowerAlphaNumeric(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAlphaNumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>一行源文本：<see cref="Text"/> 去掉行终止符，<see cref="Start"/> 为行首在原文中的下标。</summary>
    private readonly record struct SourceLine(string Text, int Start);
}
