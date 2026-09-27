using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Partner.Core.Agent.Tools;

namespace Partner.Desktop.Tools;

/// <summary>
/// Starts a program or opens a URI.
/// <para>
/// A bare name is resolved through the **Start Menu** first, and that is the whole point of this tool's
/// resolution step: <c>Process.Start</c> goes through ShellExecute, which searches <c>PATH</c> and App Paths but
/// <b>not</b> the Start Menu. So <c>notepad</c> works and <c>网易云音乐</c> — a shortcut in the Start Menu whose
/// executable lives at <c>C:\game\WangYi\CloudMusic\cloudmusic.exe</c> — does not. A person would just click the
/// shortcut; the tool has to find it.
/// </para>
/// </summary>
public sealed class DesktopLaunchTool : ITool
{
    /// <summary>Where a user's programs are listed. Both roots: machine-wide installs and per-user ones.</summary>
    private static readonly string[] DefaultRoots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
    };

    /// <summary>How many near-misses to name when nothing matches: enough to correct a typo, not a directory dump.</summary>
    private const int MaxSuggestions = 6;

    private readonly IReadOnlyList<string> _roots;

    public DesktopLaunchTool(IReadOnlyList<string>? startMenuRoots = null)
    {
        _roots = startMenuRoots ?? DefaultRoots;
    }

    public ToolSpec Spec { get; } = new(
        "desktop_launch",
        "启动一个程序或打开一个 URI。可以是可执行文件路径、URI，或**开始菜单里的程序名**（例如 '网易云音乐'、'记事本'）。"
        + "启动之后先用 desktop_observe 看它是否出现，再对它的窗口动手。",
        """
        {"type":"object","properties":{
          "target":{"type":"string","description":"可执行文件、文档、URI 或开始菜单程序名"},
          "arguments":{"type":"string","description":"可选命令行参数"}
        },"required":["target"],"additionalProperties":false}
        """);

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<LaunchArguments>(argumentsJson);
        if (arguments is null || string.IsNullOrWhiteSpace(arguments.Target))
        {
            return Task.FromResult(new ToolResult(false, "缺少 target"));
        }

        var target = arguments.Target.Trim();
        var resolved = Resolve(target);

        if (resolved is null)
        {
            return Task.FromResult(new ToolResult(false, NotFound(target)));
        }

        try
        {
            var info = new ProcessStartInfo(resolved.Value.Path)
            {
                UseShellExecute = true,
            };

            if (!string.IsNullOrWhiteSpace(arguments.Arguments))
            {
                info.Arguments = arguments.Arguments;
            }

            using var started = Process.Start(info);

            return Task.FromResult(new ToolResult(
                true,
                $"已启动 '{target}'"
                + (resolved.Value.Note is null ? string.Empty : $"（{resolved.Value.Note}）")
                + $"（pid {started?.Id.ToString() ?? "未知"}）：接下来用 desktop_observe 找它的窗口"));
        }
        catch (Exception error)
        {
            return Task.FromResult(new ToolResult(false, $"启动失败：{error.Message}"));
        }
    }

    /// <summary>
    /// What to hand the shell: the target as given, or the Start Menu entry it names. Null when a bare name
    /// matches nothing.
    /// </summary>
    public (string Path, string? Note)? Resolve(string target)
    {
        // A path, a URI, or anything with an extension is meant literally: the shell knows what to do with it.
        if (LooksLikePath(target))
        {
            return (target, null);
        }

        var match = FindInStartMenu(target);
        return match is null ? null : (match, $"开始菜单：{Path.GetFileNameWithoutExtension(match)}");
    }

    private static bool LooksLikePath(string target) =>
        target.Contains(Path.DirectorySeparatorChar)
        || target.Contains(Path.AltDirectorySeparatorChar)
        || target.Contains("://", StringComparison.Ordinal)
        || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || Path.HasExtension(target);

    /// <summary>
    /// The best Start Menu entry for a name: an exact one, else the shortest name that contains it. Shortest
    /// because "网易云音乐.lnk" beats "卸载网易云音乐.lnk" for the name "网易云音乐".
    /// </summary>
    private string? FindInStartMenu(string target)
    {
        var entries = StartMenuEntries();

        return entries
            .Where(entry => entry.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Path.Length)
            .Select(entry => entry.Path)
            .FirstOrDefault()
            ?? entries
                .Where(entry => entry.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Name.Length)
                .ThenBy(entry => entry.Path.Length)
                .Select(entry => entry.Path)
                .FirstOrDefault();
    }

    private IReadOnlyList<(string Name, string Path)> StartMenuEntries()
    {
        var entries = new List<(string, string)>();

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file);
                    if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    entries.Add((Path.GetFileNameWithoutExtension(file), file));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A Start Menu folder that cannot be read is not a reason to refuse the launch.
            }
        }

        return entries;
    }

    /// <summary>
    /// What to say when nothing matched. A model that typed "网易云" should hear "网易云音乐" back — the
    /// alternative is it guessing executable paths, which is how a launch turns into an error about a file that
    /// does not exist.
    /// </summary>
    private string NotFound(string target)
    {
        var entries = StartMenuEntries();
        var suggestions = entries
            .Where(entry => SharesSomething(entry.Name, target))
            .OrderBy(entry => Math.Abs(entry.Name.Length - target.Length))
            .Select(entry => entry.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .ToList();

        return suggestions.Count == 0
            ? $"没找到 '{target}'：它不是路径、URI，也不在开始菜单里"
            : $"没找到 '{target}'；开始菜单里有：{string.Join("、", suggestions)}";
    }

    /// <summary>True when one name contains the other, in either direction — enough to catch a partial name.</summary>
    private static bool SharesSomething(string candidate, string target) =>
        candidate.Contains(target, StringComparison.OrdinalIgnoreCase)
        || target.Contains(candidate, StringComparison.OrdinalIgnoreCase);

    private sealed record LaunchArguments(string? Target, string? Arguments);
}
