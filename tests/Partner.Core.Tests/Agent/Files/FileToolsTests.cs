using System.Diagnostics;
using System.Text;
using Partner.Core.Agent.Files;

namespace Partner.Core.Tests.Agent.Files;

/// <summary>
/// The file tools and the whitelist behind them. The whitelist tests are the ones that matter: a path that
/// merely starts with an allowed root, a <c>..</c> climb, and a link that leads out are each a way to read
/// something that was never allowed.
/// </summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
    private readonly string _root;
    private readonly string _outside;
    private readonly List<string> _links = [];

    public FileToolsTests()
    {
        _root = Path.Combine(_base, "allowed");
        _outside = Path.Combine(_base, "secret");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "diary.txt"), "不该被读到");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        // A junction is removed as the link it is: a recursive delete walks into it and is refused.
        foreach (var link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link, recursive: false);
        }

        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private FileAccessPolicy Policy(params string[] denied) => new([_root], denied);

    private static Task<Partner.Core.Agent.Tools.ToolResult> Read(FileAccessPolicy policy, string path, string extra = "") =>
        new FileReadTool(policy).InvokeAsync($$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(path)}}{{extra}}}""");

    [Fact]
    public void A_path_inside_a_root_is_allowed_and_relative_paths_start_at_the_first_root()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        Assert.True(Policy().Resolve(Path.Combine(_root, "a.txt")).Allowed);
        Assert.Equal(Path.Combine(_root, "a.txt"), Policy().Resolve("a.txt").FullPath);
    }

    [Fact]
    public void Climbing_out_with_dotdot_is_refused()
    {
        var decision = Policy().Resolve(Path.Combine("..", "secret", "diary.txt"));

        Assert.Equal(FileAccessKind.OutsideRoots, decision.Kind);
    }

    [Fact]
    public void A_sibling_directory_that_shares_the_roots_name_as_a_prefix_is_not_inside_it()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "x");

        Assert.Equal(FileAccessKind.OutsideRoots, Policy().Resolve(Path.Combine(sibling, "x.txt")).Kind);
    }

    [Fact]
    public void A_junction_that_leads_outside_the_root_is_refused()
    {
        var link = Path.Combine(_root, "shortcut");
        if (!TryMakeJunction(link, _outside))
        {
            return; // The platform cannot make one; the test has nothing to say.
        }

        var decision = Policy().Resolve(Path.Combine(link, "diary.txt"));

        Assert.Equal(FileAccessKind.OutsideRoots, decision.Kind);
    }

    [Fact]
    public async Task Reading_through_a_junction_that_leads_outside_returns_the_refusal_not_the_file()
    {
        var link = Path.Combine(_root, "shortcut");
        if (!TryMakeJunction(link, _outside))
        {
            return;
        }

        var result = await Read(Policy(), Path.Combine(link, "diary.txt"));

        Assert.False(result.Ok);
        Assert.StartsWith("outside_roots:", result.Content);
        Assert.DoesNotContain("不该被读到", result.Content);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("server.pem")]
    [InlineData("id_rsa")]
    [InlineData("vault.kdbx")]
    public async Task Credential_files_are_refused_even_inside_a_root(string name)
    {
        File.WriteAllText(Path.Combine(_root, name), "TOKEN=abc");

        var result = await Read(Policy(), name);

        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("TOKEN", result.Content);
    }

    [Fact]
    public async Task The_hosts_own_data_directory_is_refused_even_when_it_sits_inside_a_root()
    {
        var data = Path.Combine(_root, "settings");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "runtime.json"), "{\"apiKey\":\"sk-123\"}");

        var result = await Read(Policy(data), Path.Combine("settings", "runtime.json"));

        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("sk-123", result.Content);
    }

    [Fact]
    public async Task A_utf8_file_is_read_in_slices_that_say_how_to_continue()
    {
        File.WriteAllText(Path.Combine(_root, "long.txt"), new string('甲', 100));

        var first = await Read(Policy(), "long.txt", ",\"limit\":40");
        var rest = await Read(Policy(), "long.txt", ",\"offset\":80,\"limit\":40");

        Assert.True(first.Ok);
        Assert.Contains("UTF-8", first.Content);
        Assert.Contains("共 100 字符，本次 0–40", first.Content);
        Assert.Contains("用 offset=40 继续", first.Content);
        Assert.True(rest.Ok);
        Assert.DoesNotContain("继续", rest.Content);
    }

    [Fact]
    public async Task A_gbk_file_is_decoded_and_the_answer_names_the_encoding()
    {
        File.WriteAllBytes(Path.Combine(_root, "old.txt"), Encoding.GetEncoding("gb18030").GetBytes("你好，世界"));

        var result = await Read(Policy(), "old.txt");

        Assert.True(result.Ok);
        Assert.Contains("GB18030", result.Content);
        Assert.Contains("你好，世界", result.Content);
    }

    [Fact]
    public async Task A_binary_file_is_refused()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), [1, 2, 0, 3, 0, 0, 255]);

        var result = await Read(Policy(), "a.bin");

        Assert.False(result.Ok);
        Assert.StartsWith("binary:", result.Content);
    }

    [Fact]
    public async Task A_file_over_the_size_cap_is_refused_rather_than_truncated()
    {
        using (var stream = File.Create(Path.Combine(_root, "big.txt")))
        {
            stream.SetLength(FileReadTool.MaxFileBytes + 1);
        }

        var result = await Read(Policy(), "big.txt");

        Assert.False(result.Ok);
        Assert.StartsWith("too_large:", result.Content);
    }

    [Fact]
    public async Task A_missing_file_and_a_directory_are_told_apart()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var missing = await Read(Policy(), "nope.txt");
        var directory = await Read(Policy(), "sub");

        Assert.StartsWith("not_found:", missing.Content);
        Assert.StartsWith("invalid_args:", directory.Content);
        Assert.Contains("file_list", directory.Content);
    }

    [Fact]
    public async Task Listing_shows_directories_first_and_caps_a_huge_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zdir"));
        for (var i = 0; i < FileListTool.MaxEntries + 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i:D4}.txt"), "x");
        }

        var result = await new FileListTool(Policy()).InvokeAsync("{}");

        Assert.True(result.Ok);
        Assert.Contains($"共 {FileListTool.MaxEntries + 6} 项，只列前 {FileListTool.MaxEntries} 项", result.Content);
        var lines = result.Content.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        Assert.Equal("d  zdir/", lines[1]);
    }

    [Fact]
    public async Task Listing_outside_the_roots_is_refused()
    {
        var result = await new FileListTool(Policy()).InvokeAsync($$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(_outside)}}}""");

        Assert.False(result.Ok);
        Assert.StartsWith("outside_roots:", result.Content);
    }

    private bool TryMakeJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit();
        var made = process.ExitCode == 0 && Directory.Exists(link);
        if (made)
        {
            _links.Add(link);
        }

        return made;
    }
}
