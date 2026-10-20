using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// Starting a program by the name a person would say. ShellExecute searches PATH but not the Start Menu, so
/// <c>notepad</c> resolves from PATH while <c>网易云音乐</c> has to be found in the Start Menu.
/// </summary>
public sealed class DesktopLaunchToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partner-launch-tests", Guid.NewGuid().ToString("n"));

    public DesktopLaunchToolTests()
    {
        // A Start Menu as the resolver sees it: names are all it needs, so empty files stand in for shortcuts.
        Directory.CreateDirectory(Path.Combine(_root, "网易云音乐"));
        File.WriteAllText(Path.Combine(_root, "网易云音乐.lnk"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "网易云音乐", "卸载网易云音乐.lnk"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "Microsoft Edge.lnk"), string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DesktopLaunchTool Tool() => new(new[] { _root });

    [Fact]
    public void A_program_that_is_not_in_the_path_is_found_in_the_start_menu()
    {
        // The exact case: a name that exists only as a shortcut. Resolution is what this tool adds; the shell
        // follows the shortcut from there. (A stand-in shortcut is an empty file, which Windows will not start,
        // so the assertion is on what was resolved.)
        var resolved = Tool().Resolve("网易云音乐");

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(_root, "网易云音乐.lnk"), resolved!.Value.Path);
        Assert.Contains("开始菜单", resolved.Value.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exact_name_wins_over_a_longer_one_that_contains_it()
    {
        // "卸载网易云音乐" also contains the name; picking it would open an uninstaller.
        var resolved = Tool().Resolve("网易云音乐");

        Assert.NotNull(resolved);
        Assert.DoesNotContain("卸载", resolved!.Value.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_name_resolves_to_the_entry_that_contains_it()
    {
        var resolved = Tool().Resolve("网易云");

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(_root, "网易云音乐.lnk"), resolved!.Value.Path);
    }

    [Fact]
    public async Task A_name_that_matches_nothing_says_what_is_close()
    {
        // A model that typed half a name should hear the whole one back, instead of guessing an executable path.
        var result = await Tool().InvokeAsync("""{"target":"网易"}""");

        Assert.False(result.Ok);
        Assert.Contains("网易云音乐", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_that_matches_nothing_at_all_reports_the_near_misses()
    {
        var result = await Tool().InvokeAsync("""{"target":"不存在的东西xyz"}""");

        Assert.False(result.Ok);
        Assert.Contains("没找到", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_is_taken_literally_and_not_searched_for()
    {
        // A path with a separator is an instruction, not a name to look up.
        var resolved = Tool().Resolve(@"C:\definitely\missing\app.exe");

        Assert.NotNull(resolved);
        Assert.Equal(@"C:\definitely\missing\app.exe", resolved!.Value.Path);
        Assert.Null(resolved.Value.Note);
    }

    [Fact]
    public void A_uri_is_taken_literally()
    {
        var resolved = Tool().Resolve("https://example.com");

        Assert.NotNull(resolved);
        Assert.Equal("https://example.com", resolved!.Value.Path);
        Assert.Null(resolved.Value.Note);
    }
}
