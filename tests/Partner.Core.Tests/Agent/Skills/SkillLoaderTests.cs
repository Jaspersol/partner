using Partner.Core.Agent.Skills;

namespace Partner.Core.Tests.Agent.Skills;

/// <summary>
/// Workspace skill scanning and the registry: directory rules, front-matter parsing, truncation, the count
/// limit, warning determinism and lookup semantics.
/// </summary>
public sealed class SkillLoaderTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "partner-skills-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private string SkillsRoot => Path.Combine(_workspace, "skills");

    [Fact]
    public void Load_ReturnsSkillsOrderedBySkillId_WithVerbatimInstructions()
    {
        // zeta is written before alpha on purpose: the result must still sort by SkillId.
        WriteSkill("zeta", SkillFile("Zeta", "Last one", "# Zeta\n\nStep 1.\nStep 2.\n"));
        WriteSkill("alpha", SkillFile("Alpha", "First one", "# Alpha\n\n- a\n- b\n"));

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { "alpha", "zeta" }, result.Skills.Select(s => s.SkillId));

        var alpha = result.Skills[0];
        Assert.Equal("Alpha", alpha.Name);
        Assert.Equal("First one", alpha.Description);
        Assert.Equal(Path.Combine(SkillsRoot, "alpha"), alpha.Directory);
        Assert.Equal("# Alpha\n\n- a\n- b\n", alpha.Instructions);

        Assert.Equal("Zeta", result.Skills[1].Name);
        Assert.Equal("# Zeta\n\nStep 1.\nStep 2.\n", result.Skills[1].Instructions);
    }

    [Fact]
    public void Load_MissingSkillsDirectory_ReturnsEmptyWithoutWarnings()
    {
        Directory.CreateDirectory(_workspace);

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Skills);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_DirectoryWithoutSkillFile_IsSkippedWithOneWarning()
    {
        var directory = Path.Combine(SkillsRoot, "no-file");
        Directory.CreateDirectory(directory);

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Skills);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(directory, warning);
        Assert.Contains("SKILL.md", warning);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("UPPER")]
    public void Load_InvalidSkillId_IsSkippedWithOneWarning(string directoryName)
    {
        WriteSkill(directoryName, SkillFile("X", "desc"));

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Skills);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(Path.Combine(SkillsRoot, directoryName), warning);
    }

    [Fact]
    public void Load_FileWithoutFrontMatter_IsSkippedWithOneWarning()
    {
        var directory = WriteSkill("skill-a", "Just a body, no front matter.\n");

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Skills);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(Path.Combine(directory, "SKILL.md"), warning);
    }

    [Fact]
    public void Load_MissingDescription_IsSkippedWithOneWarning()
    {
        var directory = WriteSkill("skill-b", "---\nname: No Description\n---\nBody.\n");

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Skills);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(Path.Combine(directory, "SKILL.md"), warning);
        Assert.Contains("description", warning);
    }

    [Fact]
    public void Load_EmptyName_IsSkippedWithOneWarning()
    {
        var directory = WriteSkill("skill-c", "---\nname:   \ndescription: Has a description\n---\nBody.\n");

        var result = new SkillLoader().Load(_workspace);

        Assert.Empty(result.Skills);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(Path.Combine(directory, "SKILL.md"), warning);
        Assert.Contains("name", warning);
    }

    [Fact]
    public void Load_LongDescription_IsTruncatedAndStillLoaded()
    {
        WriteSkill("skill-d", SkillFile("Long", new string('x', 260)));

        var result = new SkillLoader(maxDescriptionChars: 200).Load(_workspace);

        Assert.Empty(result.Warnings);
        var skill = Assert.Single(result.Skills);
        Assert.Equal(new string('x', 200) + "…", skill.Description);
    }

    [Fact]
    public void Load_BeyondMaxSkills_DropsExtrasWithOneWarningNamingThem()
    {
        WriteSkill("alpha", SkillFile("Alpha", "First"));
        WriteSkill("beta", SkillFile("Beta", "Second"));

        var result = new SkillLoader(maxSkills: 1).Load(_workspace);

        Assert.Equal("alpha", Assert.Single(result.Skills).SkillId);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("beta", warning);
    }

    [Fact]
    public void Load_WarningsAreDeterministicAndOrderedByPath()
    {
        Directory.CreateDirectory(Path.Combine(SkillsRoot, "UPPER"));
        Directory.CreateDirectory(Path.Combine(SkillsRoot, "no-file-a"));
        Directory.CreateDirectory(Path.Combine(SkillsRoot, "no-file-b"));

        var loader = new SkillLoader();
        var first = loader.Load(_workspace).Warnings;
        var second = loader.Load(_workspace).Warnings;

        Assert.Equal(first, second);
        Assert.Equal(3, first.Count);

        // Per-path warnings are ordered by ordinal path comparison.
        var expectedOrder = new[]
        {
            Path.Combine(SkillsRoot, "UPPER"),
            Path.Combine(SkillsRoot, "no-file-a"),
            Path.Combine(SkillsRoot, "no-file-b"),
        }.OrderBy(static path => path, StringComparer.Ordinal).ToArray();

        Assert.StartsWith(expectedOrder[0], first[0]);
        Assert.StartsWith(expectedOrder[1], first[1]);
        Assert.StartsWith(expectedOrder[2], first[2]);
    }

    [Fact]
    public void Registry_FindIsCaseSensitiveOrdinal()
    {
        var registry = new InMemorySkillRegistry();
        registry.Reload([Skill("alpha")]);

        Assert.NotNull(registry.Find("alpha"));
        Assert.Null(registry.Find("Alpha"));
        Assert.Null(registry.Find("ALPHA"));
        Assert.Null(registry.Find("missing"));
    }

    [Fact]
    public void Registry_ReloadOrdersBySkillIdAndKeepsFirstDuplicate()
    {
        var winner = Skill("beta", name: "Winner");
        var loser = Skill("beta", name: "Loser");

        var registry = new InMemorySkillRegistry();
        registry.Reload([winner, Skill("alpha"), loser]);

        Assert.Equal(new[] { "alpha", "beta" }, registry.Skills.Select(s => s.SkillId));
        Assert.Same(winner, registry.Find("beta"));
    }

    [Fact]
    public void Registry_ReloadReplacesTheWholeSet()
    {
        var registry = new InMemorySkillRegistry();
        registry.Reload([Skill("alpha")]);
        registry.Reload([Skill("beta")]);

        Assert.Equal(new[] { "beta" }, registry.Skills.Select(s => s.SkillId));
        Assert.Null(registry.Find("alpha"));
    }

    /// <summary>Writes a skill directory and its <c>SKILL.md</c>, then returns the directory path.</summary>
    private string WriteSkill(string directoryName, string content)
    {
        var directory = Path.Combine(SkillsRoot, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        return directory;
    }

    private static string SkillFile(string name, string description, string body = "Do the thing.\n") =>
        $"---\nname: {name}\ndescription: {description}\n---\n{body}";

    private static SkillDefinition Skill(string skillId, string name = "Name", string description = "desc") => new()
    {
        SkillId = skillId,
        Name = name,
        Description = description,
        Directory = Path.Combine(Path.GetTempPath(), "partner-skills-tests", skillId),
        Instructions = "body",
    };
}
