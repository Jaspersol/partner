namespace Partner.Core.Agent.Skills;

/// <summary>The result of one workspace skill scan.</summary>
/// <param name="Skills">Skills after the count limit is applied, ordered by <see cref="SkillDefinition.SkillId"/> ascending.</param>
/// <param name="Warnings">
/// Why a skill was skipped or dropped. Order is deterministic: per-path warnings sort by path ordinal
/// (<see cref="StringComparer.Ordinal"/>), and the count-limit warning is appended last.
/// </param>
public sealed record SkillLoadResult(IReadOnlyList<SkillDefinition> Skills, IReadOnlyList<string> Warnings);

/// <summary>
/// Workspace skill scanner.
/// <para>
/// Recognises only <c>&lt;workspaceDirectory&gt;/skills/&lt;skillId&gt;/SKILL.md</c>. When the <c>skills</c>
/// directory is absent it returns an empty result with <b>no warning</b>. A directory name not matching
/// <c>^[a-z0-9][a-z0-9._-]*$</c>, a missing <c>SKILL.md</c>, missing or unclosed front matter, or a missing or
/// empty required field records a warning and skips that skill.
/// </para>
/// <para>
/// <b>File format</b>: the first line must be exactly <c>---</c>, and the front matter ends at the next line
/// that is exactly <c>---</c>. Keys are <c>name</c> and <c>description</c> (case-insensitive, values trimmed),
/// both required; unknown keys, blank lines, and lines without <c>:</c> are silently ignored.
/// <c>description</c> is single-line by construction; longer than <c>maxDescriptionChars</c> it is truncated and
/// <c>…</c> appended. <see cref="SkillDefinition.Instructions"/> is everything after the closing delimiter, with
/// at most one leading newline stripped.
/// </para>
/// <para>Invalid content never throws; it degrades to a warning. I/O failures on the files themselves still throw.</para>
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

    /// <summary>Creates the scanner.</summary>
    /// <param name="maxSkills">Maximum number of skills returned; the excess is dropped in <see cref="SkillDefinition.SkillId"/> ascending order with one warning.</param>
    /// <param name="maxDescriptionChars">Maximum description length; the excess is truncated and <c>…</c> appended.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxSkills"/> is negative, or <paramref name="maxDescriptionChars"/> is less than 1.
    /// </exception>
    public SkillLoader(int maxSkills = 50, int maxDescriptionChars = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSkills);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDescriptionChars, 1);

        _maxSkills = maxSkills;
        _maxDescriptionChars = maxDescriptionChars;
    }

    /// <summary>Scans <c>&lt;workspaceDirectory&gt;/skills/*/SKILL.md</c>. Invalid content does not throw.</summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceDirectory"/> is null, empty, or whitespace.</exception>
    public SkillLoadResult Load(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);

        // Directory in the result must be absolute, so normalise the root first.
        var skillsDirectory = Path.GetFullPath(Path.Combine(workspaceDirectory, SkillsDirectoryName));
        if (!Directory.Exists(skillsDirectory))
        {
            // A missing skills directory is not an error and produces no warning.
            return new SkillLoadResult(Array.Empty<SkillDefinition>(), Array.Empty<string>());
        }

        var warnings = new List<string>();
        var skills = new List<SkillDefinition>();

        // The directory name is the SkillId; sort by path ordinal first so warning order is deterministic
        // (enumeration order depends on the file system).
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
    /// Parses one <c>SKILL.md</c>; invalid content records a warning and returns <see langword="null"/>.
    /// </summary>
    private SkillDefinition? Parse(string skillId, string directory, string skillFile, List<string> warnings)
    {
        var text = File.ReadAllText(skillFile);

        // Tolerate a UTF-8 BOM: otherwise the first line would not be exactly ---.
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
            // Blank lines and lines without ':' are silently ignored.
            var line = lines[i].Text.Trim();
            var separator = line.IndexOf(':');
            if (line.Length == 0 || separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // Keys are case-insensitive; unknown keys are ignored. A duplicate key takes the later value.
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

    /// <summary>Everything after the closing delimiter, with at most one leading newline (the closing line's terminator) stripped.</summary>
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

    /// <summary>Splits into lines, recording each line's text without its terminator and its start index (handles <c>\n</c> and <c>\r\n</c>).</summary>
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

    /// <summary>Whether the line is exactly the front-matter delimiter (a trailing <c>\r</c> is already removed).</summary>
    private static bool IsDelimiter(SourceLine line) =>
        line.Text.AsSpan().SequenceEqual(FrontMatterDelimiter);

    /// <summary>Skill-id check: equivalent to <c>^[a-z0-9][a-z0-9._-]*$</c>.</summary>
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

    /// <summary>One source line: <see cref="Text"/> without its terminator, <see cref="Start"/> its index in the source.</summary>
    private readonly record struct SourceLine(string Text, int Start);
}
