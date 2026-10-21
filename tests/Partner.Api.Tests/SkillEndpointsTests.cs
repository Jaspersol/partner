using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// Adding and removing skills over HTTP: what is written, what is refused, and that nothing half-written is left
/// behind for the scanner to complain about.
/// </summary>
public sealed class SkillEndpointsTests : ApiFixture
{
    protected override string WorkspaceName => "partner-skills-tests";

    protected override string ReadinessPath => "/api/skills";

    private string SkillFile(string id) => Path.Combine(_workspace, "skills", id, "SKILL.md");

    private Task<HttpResponseMessage> CreateAsync(string id, string name = "Reviewer", string description = "Reviews a change", string instructions = "Read the diff.") =>
        _http.PostAsJsonAsync("/api/skills", new { skillId = id, name, description, instructions });

    private async Task<HttpResponseMessage> UploadAsync(string fileName, byte[] bytes)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        return await _http.PostAsync("/api/skills/upload", content);
    }

    private static byte[] Markdown(string name = "Uploaded", string description = "From a file") =>
        Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: {description}\n---\nDo it.\n");

    private static byte[] Zip(params (string Path, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }

    private async Task<List<string>> SkillIdsAsync() =>
        (await _http.GetFromJsonAsync<JsonElement>("/api/skills")).GetProperty("skills")
            .EnumerateArray().Select(skill => skill.GetProperty("skillId").GetString()!).ToList();

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    [Fact]
    public async Task Creating_a_skill_writes_a_SKILL_md_the_scanner_accepts()
    {
        var response = await CreateAsync("code-review", "Code review", "Reviews a change", "Read the diff.\n\nBe kind.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listing = await _http.GetFromJsonAsync<JsonElement>("/api/skills");
        var skill = Assert.Single(listing.GetProperty("skills").EnumerateArray());
        Assert.Equal("code-review", skill.GetProperty("skillId").GetString());
        Assert.Equal("Code review", skill.GetProperty("name").GetString());
        Assert.Contains("Be kind.", skill.GetProperty("instructions").GetString());
        Assert.Empty(listing.GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData("Has Capitals")]
    [InlineData("../escape")]
    [InlineData("-leading")]
    [InlineData("")]
    public async Task Creating_refuses_an_id_that_is_not_a_plain_lower_case_name(string id)
    {
        var response = await CreateAsync(id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_workspace, "skills")) && Directory.EnumerateDirectories(Path.Combine(_workspace, "skills")).Any());
    }

    [Fact]
    public async Task Creating_refuses_a_name_or_description_that_would_break_the_front_matter()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync("a", name: "two\nlines")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync("a", description: "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync("a", instructions: "  ")).StatusCode);
        Assert.False(File.Exists(SkillFile("a")));
    }

    [Fact]
    public async Task Creating_never_overwrites_an_existing_skill()
    {
        await CreateAsync("same", instructions: "first");

        var again = await CreateAsync("same", instructions: "second");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("first", await File.ReadAllTextAsync(SkillFile("same")));
    }

    [Fact]
    public async Task Uploading_a_markdown_file_names_the_skill_after_the_file()
    {
        var response = await UploadAsync("my-skill.md", Markdown());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("my-skill", await SkillIdsAsync());
    }

    [Fact]
    public async Task Uploading_a_bare_SKILL_md_falls_back_to_the_skill_name()
    {
        var response = await UploadAsync("SKILL.md", Markdown(name: "Report Writer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("report-writer", await SkillIdsAsync());
    }

    [Fact]
    public async Task Uploading_a_zip_keeps_the_files_beside_SKILL_md()
    {
        var archive = Zip(("SKILL.md", Encoding.UTF8.GetString(Markdown())), ("scripts/run.txt", "hello"));

        var response = await UploadAsync("packed.zip", archive);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_workspace, "skills", "packed", "scripts", "run.txt")));
    }

    [Fact]
    public async Task Uploading_a_zip_that_holds_one_folder_uses_the_folder_as_the_id()
    {
        var archive = Zip(("Nice-Skill/SKILL.md", Encoding.UTF8.GetString(Markdown())));

        var response = await UploadAsync("whatever.zip", archive);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("nice-skill", await SkillIdsAsync());
    }

    [Fact]
    public async Task Uploading_refuses_what_the_scanner_would_only_warn_about_and_leaves_nothing_behind()
    {
        var noFrontMatter = await UploadAsync("bad.md", Encoding.UTF8.GetBytes("just text"));
        var noSkillFile = await UploadAsync("empty.zip", Zip(("readme.txt", "hi")));
        var notAZip = await UploadAsync("broken.zip", Encoding.UTF8.GetBytes("not a zip"));
        var wrongType = await UploadAsync("skill.exe", new byte[] { 1, 2, 3 });

        Assert.All(new[] { noFrontMatter, noSkillFile, notAZip, wrongType }, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Contains("这不是有效的技能", await ErrorAsync(noFrontMatter));
        Assert.Empty(await SkillIdsAsync());
        Assert.False(Directory.Exists(Path.Combine(_workspace, ".skill-staging")));
    }

    [Fact]
    public async Task Uploading_refuses_an_archive_that_climbs_out_of_the_skill_folder()
    {
        var archive = Zip(("SKILL.md", Encoding.UTF8.GetString(Markdown())), ("../../outside.txt", "gotcha"));

        var response = await UploadAsync("evil.zip", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_workspace, "..", "outside.txt")));
        Assert.Empty(await SkillIdsAsync());
    }

    [Fact]
    public async Task Uploading_over_an_existing_id_is_a_conflict_not_a_replacement()
    {
        await CreateAsync("taken", instructions: "original");

        var response = await UploadAsync("taken.md", Markdown());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("original", await File.ReadAllTextAsync(SkillFile("taken")));
    }

    [Fact]
    public async Task Deleting_removes_the_folder_and_the_deny_list_entry()
    {
        await CreateAsync("doomed");
        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        var withDenied = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText())!;
        withDenied["skills"] = new { disabled = new[] { "doomed" } };
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync("/api/settings", withDenied)).StatusCode);

        var response = await _http.DeleteAsync("/api/skills/doomed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_workspace, "skills", "doomed")));
        Assert.Empty(await SkillIdsAsync());
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Empty(after.GetProperty("skills").GetProperty("disabled").EnumerateArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("..")]
    [InlineData("UPPER")]
    public async Task Deleting_something_that_is_not_a_skill_is_not_found(string id)
    {
        var response = await _http.DeleteAsync($"/api/skills/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(Directory.Exists(_workspace));
    }
}
