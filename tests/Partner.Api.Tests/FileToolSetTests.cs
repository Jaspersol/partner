using System.Text.Json;
using Partner.Api.Agents;
using Partner.Api.Settings;
using Partner.Core.Agent.Tools;

namespace Partner.Api.Tests;

public sealed class FileToolSetTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "partner-file-tool-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_base))
            Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public void Write_registration_requires_enabled_windows_capability()
    {
        var workspace = Path.Combine(_base, "workspace");
        var root = Path.Combine(_base, "root");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(root);
        var store = new SettingsStore(Path.Combine(workspace, "runtime.json"));
        store.Save(store.Load() with { Files = new FilesSettings
        {
            Enabled = true, Roots = [root], WriteEnabled = true, WriteRoots = [root],
        } });
        var tools = new InMemoryToolRegistry();
        FileToolSet.Register(tools, store, store.Load(), workspace);
        Assert.Equal(OperatingSystem.IsWindows(), tools.Find("file_write") is not null);
    }

    [Fact]
    public async Task Prepared_write_rechecks_authorization_before_commit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var workspace = Path.Combine(_base, "workspace");
        var root = Path.Combine(_base, "root");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(root);
        var store = new SettingsStore(Path.Combine(workspace, "runtime.json"));
        store.Save(store.Load() with { Files = new FilesSettings
        {
            Enabled = true, Roots = [root], WriteEnabled = true, WriteRoots = [root],
        } });
        var tools = new InMemoryToolRegistry();
        FileToolSet.Register(tools, store, store.Load(), workspace);
        var writer = Assert.IsAssignableFrom<IPreparedFileWriteTool>(tools.Find("file_write"));
        var target = Path.Combine(root, "revoked.txt");
        using var prepared = writer.Prepare(JsonSerializer.Serialize(new { path = target, content = "private" }), default);
        store.Save(store.Load() with { Files = store.Load().Files with { WriteEnabled = false } });
        var result = await prepared.CommitAsync(default);
        Assert.False(result.Ok);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Prepared_edit_rechecks_authorization_before_commit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var workspace = Path.Combine(_base, "workspace");
        var root = Path.Combine(_base, "root");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "original.txt");
        await File.WriteAllTextAsync(target, "original");
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(target))).ToLowerInvariant();
        var store = new SettingsStore(Path.Combine(workspace, "runtime.json"));
        store.Save(store.Load() with { Files = new FilesSettings
        {
            Enabled = true, Roots = [root], WriteEnabled = true, WriteRoots = [root],
        } });
        var editor = new LiveFileEditTool(store, workspace, null);
        using var prepared = editor.Prepare(JsonSerializer.Serialize(new
        {
            path = target, old_text = "original", new_text = "replacement", expected_version = version,
        }), default);
        using var approval = JsonDocument.Parse(prepared.ApprovalJson);
        Assert.Contains("读取版本不一致时，不会提交", approval.RootElement.GetProperty("risk").GetString(), StringComparison.Ordinal);
        store.Save(store.Load() with { Files = store.Load().Files with { WriteEnabled = false } });
        var result = await prepared.CommitAsync(default);
        Assert.False(result.Ok);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData("file_list")]
    [InlineData("file_read")]
    public async Task Existing_tools_follow_current_roots_capability_and_owner(string name)
    {
        var first = Path.Combine(_base, "first");
        var second = Path.Combine(_base, "second");
        var workspace = Path.Combine(_base, "workspace");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(first, "a.txt"), "first");
        File.WriteAllText(Path.Combine(second, "a.txt"), "second");
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "secret");

        var store = new SettingsStore(Path.Combine(workspace, "runtime.json"));
        var initial = store.Load();
        store.Save(initial with { Files = new FilesSettings { Enabled = true, Roots = [first] } });
        var main = new InMemoryToolRegistry();
        FileToolSet.Register(main, store, store.Load(), workspace);
        var mainTool = main.Find(name)!;

        string Args(string root) => JsonSerializer.Serialize(new
        {
            path = name == "file_read" ? Path.Combine(root, "a.txt") : root,
        });
        async Task<bool> CanRead(ITool tool, string root) => (await tool.InvokeAsync(Args(root))).Ok;

        Assert.True(await CanRead(mainTool, first));
        Assert.False(await CanRead(mainTool, second));
        store.Save(store.Load() with { Files = new FilesSettings { Enabled = true, Roots = [second, workspace] } });
        Assert.False(await CanRead(mainTool, first));
        Assert.True(await CanRead(mainTool, second));
        Assert.False(await CanRead(mainTool, workspace));

        var reader = new SubAgentSettings { Id = "reader", Name = "文件阅读", Scenario = AgentScenarios.FileRead };
        var agents = initial.Agents with { SubAgents = [.. initial.Agents.SubAgents, reader] };
        store.Save(store.Load() with { Agents = agents });
        Assert.False(await CanRead(mainTool, second));
        var sub = new InMemoryToolRegistry();
        FileToolSet.Register(sub, store, store.Load(), workspace, reader.Id);
        var subTool = sub.Find(name)!;
        store.Save(store.Load() with { Files = new FilesSettings { Enabled = true, Roots = [first] } });
        Assert.False(await CanRead(subTool, second));
        Assert.True(await CanRead(subTool, first));
        store.Save(store.Load() with { Files = new FilesSettings { Enabled = true, Roots = [second] } });

        store.Save(store.Load() with { Agents = agents with { SubAgents = [.. initial.Agents.SubAgents, reader with { Enabled = false }] } });
        Assert.False(await CanRead(subTool, second));
        Assert.True(await CanRead(mainTool, second));
        store.Save(store.Load() with { Agents = agents });
        Assert.True(await CanRead(subTool, second));

        store.Save(store.Load() with { Agents = agents with { SubAgents = [.. initial.Agents.SubAgents, reader with { Id = "other" }] } });
        Assert.False(await CanRead(subTool, second));
        Assert.False(await CanRead(mainTool, second));
        store.Save(store.Load() with { Agents = initial.Agents });
        Assert.False(await CanRead(subTool, second));
        Assert.True(await CanRead(mainTool, second));

        store.Save(store.Load() with { Files = new FilesSettings { Enabled = false, Roots = [second] } });
        Assert.False(await CanRead(mainTool, second));
        Assert.False(await CanRead(subTool, second));
    }
}
