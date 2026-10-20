using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;

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

    /// <summary>
    /// Where launched debug ports are recorded. Null leaves the DOM path off, which is the honest default for a
    /// tool built without one: a window launched with a flag nobody recorded has an unreachable DOM, and that
    /// looks exactly like an application with no controls.
    /// </summary>
    private readonly CdpRegistry? _ports;

    /// <summary>
    /// Whether this launch may take the screen. Read at launch time, not construction: the mode is a setting
    /// and it can change while the process runs. Null is the foreground behaviour every earlier build had.
    /// </summary>
    private readonly Func<InputMode>? _inputMode;

    public DesktopLaunchTool(
        IReadOnlyList<string>? startMenuRoots = null,
        CdpRegistry? ports = null,
        Func<InputMode>? inputMode = null)
    {
        _roots = startMenuRoots ?? DefaultRoots;
        _ports = ports;
        _inputMode = inputMode;
    }

    public ToolSpec Spec { get; } = new(
        "desktop_launch",
        "启动一个程序或打开一个 URI。可以是可执行文件路径、URI，或**开始菜单里的程序名**（例如 '网易云音乐'、'记事本'）。"
        + "启动之后先用 desktop_observe 看它是否出现，再对它的窗口动手。"
        + "浏览器/Electron 程序（chrome、msedge、electron 等）会附带开一个页面调试端口，于是它的按钮、链接、"
        + "输入框会作为控件出现在 desktop_observe 里，可以直接按名字点，而不用靠坐标。"
        + "别的 Electron 程序（比如网易云音乐的 cloudmusic.exe）名字里看不出引擎，在 arguments 里自己传 "
        + "--remote-debugging-port=9222 也一样能用。",
        """
        {"type":"object","properties":{
          "target":{"type":"string","description":"可执行文件、文档、URI 或开始菜单程序名"},
          "arguments":{"type":"string","description":"可选命令行参数；要读取 Electron 程序的页面控件，就在这里传 --remote-debugging-port=9222"}
        },"required":["target"],"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<LaunchArguments>(argumentsJson);
        if (arguments is null || string.IsNullOrWhiteSpace(arguments.Target))
        {
            return new ToolResult(false, "缺少 target");
        }

        var target = arguments.Target.Trim();
        var resolved = Resolve(target);

        if (resolved is null)
        {
            return new ToolResult(false, NotFound(target));
        }

        try
        {
            return await LaunchAsync(target, resolved.Value, arguments.Arguments, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"启动失败：{error.Message}");
        }
    }

    /// <summary>
/// The port a caller already asked for, if the arguments carry one.
/// <para>
/// This is what makes an Electron application reachable whatever it is called. The names
/// <see cref="CdpRegistry.IsChromiumFamily"/> recognises are only the ones that can be told on sight; an
/// Electron app is named after its product — 网易云音乐 is <c>cloudmusic.exe</c>, and nothing in that string
/// says Chromium — so without reading the flag here that application's DOM stays unreachable however many
/// names are added to a list. It is also the honest way to reach an application the user chose to start with
/// the flag themselves.
/// </para>
/// </summary>
internal static int? RequestedPort(string? arguments)
{
    if (string.IsNullOrWhiteSpace(arguments))
    {
        return null;
    }

    // Both spellings exist in the wild: --remote-debugging-port=9222 and --remote-debugging-port 9222.
    var match = System.Text.RegularExpressions.Regex.Match(
        arguments,
        @"--remote-debugging-port[= ](\d{1,5})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and <= 65535
        ? port
        : null;
}

/// <summary>
    /// Starts the program and, for a browser, waits for the port it was asked to open.
    /// <para>
    /// The wait is bounded and its failure is reported, because the likeliest reason is not a bug: a
    /// single-instance application that was already running takes the new command line and never opens a port.
    /// Saying so is the difference between "this application has no controls" and "close it and start it
    /// through me".
    /// </para>
    /// </summary>
    private async Task<ToolResult> LaunchAsync(
        string target,
        (string Path, string? Note) resolved,
        string? arguments,
        CancellationToken ct)
    {
        var background = (_inputMode?.Invoke() ?? InputMode.Foreground) != InputMode.Foreground;

        // A port the caller already asked for is used as it stands. Adding a second one would be a duplicate
        // flag, and which of the two an application honours is not worth guessing.
        var asked = RequestedPort(arguments);
        var port = asked ?? 0;
        var chromium = asked is not null || CdpRegistry.IsChromiumFamily(resolved.Path);

        var commandLine = arguments;
        if (chromium && asked is null)
        {
            port = CdpRegistry.FreePort();
            commandLine = string.IsNullOrEmpty(commandLine)
                ? $"--remote-debugging-port={port}"
                : $"{commandLine} --remote-debugging-port={port}";
        }

        if (background)
        {
            return await LaunchInBackgroundAsync(target, resolved, commandLine, chromium, port, ct).ConfigureAwait(false);
        }

        var info = new ProcessStartInfo(resolved.Path)
        {
            UseShellExecute = true,
        };

        if (!string.IsNullOrEmpty(commandLine))
        {
            info.Arguments = commandLine;
        }

        using var started = Process.Start(info);

        if (started is null)
        {
            return new ToolResult(false, $"启动 '{target}' 失败：系统没有返回进程");
        }

        if (!chromium)
        {
            return BuildResult(target, resolved.Note, started.Id, chromium: false, opened: false, port: 0);
        }

        // Recorded only once the port has actually answered: registering the intention would leave a window
        // whose DOM is claimed to be readable and is not.
        var opened = _ports is not null
            && await _ports.WaitForPortAsync(started.Id, port, ct).ConfigureAwait(false);

        if (opened)
        {
            _ports!.Register(started.Id, port);
        }

        return BuildResult(target, resolved.Note, started.Id, chromium: true, opened, port);
    }

    /// <summary>
    /// The same launch, without taking the screen: started with "show, don't activate", its windows sent to the
    /// bottom of the z-order, and the foreground handed back if the program grabbed it.
    /// </summary>
    private async Task<ToolResult> LaunchInBackgroundAsync(
        string target,
        (string Path, string? Note) resolved,
        string? commandLine,
        bool chromium,
        int port,
        CancellationToken ct)
    {
        // Behind other windows a browser decides it is covered and stops painting; keep it rendering so its
        // picture and its page's timers stay live while nobody is looking at it.
        if (chromium)
        {
            commandLine = BackgroundLaunch.WithRenderingFlags(commandLine);
        }

        var before = BackgroundLaunch.Snapshot.Take();
        var processId = BackgroundLaunch.Start(resolved.Path, commandLine);

        // Settled alongside the port wait rather than after it: the window appears while the port is still
        // opening, and parking it late is parking it after the user already saw it.
        var settling = BackgroundLaunch.SettleAsync(before, processId, ct);

        var opened = false;
        if (chromium && processId != 0 && _ports is not null)
        {
            opened = await _ports.WaitForPortAsync(processId, port, ct).ConfigureAwait(false);
            if (opened)
            {
                _ports.Register(processId, port);
            }
        }

        var placement = BackgroundLaunch.Describe(await settling.ConfigureAwait(false));
        var result = BuildResult(target, resolved.Note, processId, chromium, opened, chromium ? port : 0);

        return new ToolResult(result.Ok, $"{result.Content}\n{placement}");
    }

    /// <summary>
    /// What the launch says, including whether the DOM ended up reachable.
    /// <para>
    /// Pure so the three outcomes can be asserted without starting anything: the port opened, the flag was
    /// handed to an already-running single-instance process, or this is not a browser at all and the flag was
    /// never attempted. Only the middle one is a problem, and it says what to do about it.
    /// </para>
    /// </summary>
    public static ToolResult BuildResult(
        string target,
        string? note,
        int processId,
        bool chromium,
        bool opened,
        int port)
    {
        var head = $"已启动 '{target}'"
            + (note is null ? string.Empty : $"（{note}）")
            + $"（pid {processId}）";

        if (!chromium)
        {
            return new ToolResult(true, $"{head}：接下来用 desktop_observe 找它的窗口");
        }

        return opened
            ? new ToolResult(
                true,
                $"{head}，页面调试端口 {port} 已打开：它的按钮、链接、输入框会作为控件出现在 desktop_observe 里，"
                + "直接按名字用 elementId 点即可，不需要窗口在前台。")
            : new ToolResult(
                true,
                $"{head}，已请求 --remote-debugging-port={port} 但端口没有打开——最常见的原因是**这个程序是单实例的、"
                + "已经在运行**：新命令行被交给了已在运行的进程，它不会新开端口。此时它的控件仍要走坐标路径；"
                + "把它关掉，再用 desktop_launch 启动，页面控件就会出现。");
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
