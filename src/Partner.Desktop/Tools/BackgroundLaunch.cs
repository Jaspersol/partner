using System.ComponentModel;
using System.Runtime.InteropServices;
using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tools;

/// <summary>
/// Starting a program without it landing on top of what the user is doing.
/// <para>
/// A window opened by <c>ShellExecute</c> with the default show command activates itself: the user's editor
/// loses the keyboard in the middle of a sentence. This starts the program with the "show, don't activate"
/// command, then watches for the windows it opens and sends them to the bottom of the z-order — and if the
/// program took the foreground anyway (many do, whatever they were asked), gives it back to whoever had it.
/// </para>
/// <para>
/// None of this is a guarantee, and nothing here claims it is: "don't activate" is a request the program can
/// ignore. What the settle step reports is what it actually saw and did.
/// </para>
/// </summary>
internal static class BackgroundLaunch
{
    /// <summary>
    /// Starts <paramref name="path"/> with <c>SW_SHOWNOACTIVATE</c>. Returns the process id, or 0 when the shell
    /// handled the request without handing back a process (it was passed to one already running).
    /// </summary>
    /// <exception cref="Win32Exception">The shell refused to start it.</exception>
    internal static int Start(string path, string? arguments)
    {
        var info = new NativeMethods.ShellExecuteInfo
        {
            Size = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
            Mask = NativeMethods.SeeMaskNoCloseProcess | NativeMethods.SeeMaskFlagNoUi,
            File = path,
            Parameters = string.IsNullOrEmpty(arguments) ? null : arguments,
            Show = NativeMethods.SwShowNoActivate,
        };

        if (!NativeMethods.ShellExecuteEx(ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (info.Process == 0)
        {
            return 0;
        }

        try
        {
            return (int)NativeMethods.GetProcessId(info.Process);
        }
        finally
        {
            NativeMethods.CloseHandle(info.Process);
        }
    }

    /// <summary>
    /// The flags that keep a Chromium window rendering while something else is on top of it.
    /// <para>
    /// A window the browser believes is covered stops painting and throttles its timers: its picture goes stale
    /// and its page stops animating, which from outside looks like an application that hung. These two switches
    /// turn that off. They are merged into an existing <c>--disable-features</c> rather than added beside it,
    /// because a repeated switch is not a union — the last one wins and would silently drop the other's list.
    /// </para>
    /// </summary>
    internal static string WithRenderingFlags(string? arguments)
    {
        const string Feature = "CalculateNativeWinOcclusion";
        const string Backgrounding = "--disable-backgrounding-occluded-windows";

        var result = arguments?.Trim() ?? string.Empty;

        if (!result.Contains(Backgrounding, StringComparison.OrdinalIgnoreCase))
        {
            result = $"{result} {Backgrounding}".Trim();
        }

        var existing = System.Text.RegularExpressions.Regex.Match(
            result,
            @"--disable-features=([^\s""]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!existing.Success)
        {
            return $"{result} --disable-features={Feature}".Trim();
        }

        var features = existing.Groups[1].Value;
        if (features.Split(',').Contains(Feature, StringComparer.OrdinalIgnoreCase))
        {
            return result;
        }

        var merged = string.IsNullOrEmpty(features) ? Feature : $"{features},{Feature}";
        return result.Remove(existing.Groups[1].Index, existing.Groups[1].Length).Insert(existing.Groups[1].Index, merged);
    }

    /// <summary>
    /// The windows in <paramref name="now"/> that belong to the program that was just started.
    /// <para>
    /// "New since the launch" is not enough: in the seconds this watches, the user may open a window of their
    /// own, and sending <i>that</i> to the bottom — or taking the foreground back from it — would be the agent
    /// interfering with exactly the person it is trying not to disturb. So a new window counts only if its
    /// process is the one started, or was itself started after the launch began (a launcher that hands off to
    /// the real program, an updater stub). A new window of a program that was already running is not ours.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DesktopWindow> LaunchedWindows(Snapshot before, int processId, IEnumerable<DesktopWindow> now) =>
        now.Where(window => !before.Windows.Contains(window.Handle)
                && (window.ProcessId == processId
                    || (window.ProcessStartTicks is { } started && started >= before.StartedAtTicks)))
            .ToList();

    /// <summary>Whether a window belongs to the started program, by its process (see <see cref="LaunchedWindows"/>).</summary>
    private static bool IsLaunched(nint handle, Snapshot before, int processId)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var pid);

        return (int)pid == processId
            || (WindowEnumerator.TryGetProcessStartTicks(pid) is { } started && started >= before.StartedAtTicks);
    }

    /// <summary>
    /// What the user had in front of them before the launch, so it can be given back.
    /// </summary>
    internal sealed record Snapshot(nint Foreground, IReadOnlySet<nint> Windows, long StartedAtTicks)
    {
        internal static Snapshot Take() => new(
            NativeMethods.GetForegroundWindow(),
            WindowEnumerator.List().Select(window => window.Handle).ToHashSet(),
            DateTime.UtcNow.Ticks);
    }

    /// <summary>What the settle step saw and did, for the launch result to say plainly.</summary>
    internal sealed record Outcome(int WindowsParked, bool ForegroundRestored, bool ForegroundTaken);

    /// <summary>
    /// Watches for the launched program's windows for a bounded time, sends each to the bottom of the z-order
    /// without activating it, and returns the foreground if the program took it.
    /// <para>
    /// Polled rather than hooked: a window hook needs a message loop of its own and this runs in a service. It
    /// stops once windows have appeared and then stayed quiet for a few polls — a splash screen that is
    /// replaced by the real window is the common case, and each new window is parked as it shows up.
    /// </para>
    /// </summary>
    internal static async Task<Outcome> SettleAsync(Snapshot before, int processId, CancellationToken ct, int budgetMs = 5000, int pollMs = 150)
    {
        var parked = new HashSet<nint>();
        var taken = false;
        var restored = false;
        var quiet = 0;
        var waited = 0;

        while (waited < budgetMs && !ct.IsCancellationRequested)
        {
            var fresh = LaunchedWindows(before, processId, WindowEnumerator.List())
                .Where(window => !parked.Contains(window.Handle))
                .ToList();

            foreach (var window in fresh)
            {
                Park(window.Handle);
                parked.Add(window.Handle);
            }

            // Only a launched window holding the foreground is the program taking it. A foreground the user
            // chose — they clicked elsewhere while this was watching — is theirs and is left alone.
            // The foreground holder is judged by its process, not by whether it was among the windows listed: a
            // browser shows an untitled window first (measured), which the listing skips and which took the
            // foreground all the same.
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != before.Foreground && foreground != 0 && IsLaunched(foreground, before, processId))
            {
                if (parked.Add(foreground))
                {
                    Park(foreground);
                }

                taken = true;
                if (before.Foreground != 0 && NativeMethods.IsWindow(before.Foreground))
                {
                    restored = new InputInjector().Focus(before.Foreground).Ok;
                }
            }

            quiet = fresh.Count == 0 ? quiet + 1 : 0;
            if (parked.Count > 0 && quiet >= 8)
            {
                break;
            }

            try
            {
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            waited += pollMs;
        }

        return new Outcome(parked.Count, restored, taken);
    }

    /// <summary>Behind everything, not activated, not moved or resized — the window stays where it opened.</summary>
    private static void Park(nint handle) =>
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndBottom,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);

    /// <summary>The sentence the launch result carries about what happened to the user's screen.</summary>
    internal static string Describe(Outcome outcome) => outcome switch
    {
        { WindowsParked: 0 } => "后台启动：没有看到新窗口出现（程序可能还在启动，或交给了已在运行的进程）",
        { ForegroundTaken: true, ForegroundRestored: true } =>
            $"后台启动：{outcome.WindowsParked} 个新窗口已放到最底层；程序抢了前台，已还给原来的窗口",
        { ForegroundTaken: true } =>
            $"后台启动：{outcome.WindowsParked} 个新窗口已放到最底层；但程序抢了前台，还回去没成功",
        _ => $"后台启动：{outcome.WindowsParked} 个新窗口已放到最底层，没有动你的前台窗口",
    };
}
