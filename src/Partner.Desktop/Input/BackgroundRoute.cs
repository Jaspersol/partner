namespace Partner.Desktop.Input;

/// <summary>Where an action that names no pattern-addressable control is carried.</summary>
public enum Route
{
    /// <summary>The foreground path: raise the window if it is covered, move the real pointer, inject.</summary>
    Legacy,

    /// <summary>The page's debug protocol: events delivered to the page itself.</summary>
    Cdp,

    /// <summary>"Focus" as far as it can go without the foreground: restore a minimized window, activate nothing.</summary>
    RestoreOnly,

    /// <summary>Nothing that leaves the foreground alone can carry this action, and the mode forbids the rest.</summary>
    Refuse,
}

/// <summary>
/// The decision that turns an <see cref="InputMode"/> into a channel.
/// <para>
/// Pure and separate from the code that sends anything, so the policy can be read — and pinned — as a table: the
/// cost of getting it wrong is either an agent that takes over the user's screen when it was told not to, or one
/// that refuses something a background channel could have done.
/// </para>
/// </summary>
public static class BackgroundRoute
{
    /// <param name="mode">What the setting allows.</param>
    /// <param name="kind">The action.</param>
    /// <param name="hasPage">Whether the target window has a page debug port this agent can reach.</param>
    public static Route Decide(InputMode mode, ActionKind kind, bool hasPage)
    {
        if (mode == InputMode.Foreground)
        {
            return Route.Legacy;
        }

        // Not an input at all: it asks a control to scroll itself, and a pattern or nothing answers it.
        if (kind == ActionKind.ScrollIntoView)
        {
            return mode == InputMode.Background ? Route.Refuse : Route.Legacy;
        }

        if (hasPage)
        {
            return Route.Cdp;
        }

        if (kind == ActionKind.Focus)
        {
            return mode == InputMode.Background ? Route.RestoreOnly : Route.Legacy;
        }

        return mode == InputMode.Background ? Route.Refuse : Route.Legacy;
    }

    /// <summary>
    /// What the refusal says. It names the ways out, because "refused" alone sends a model back to try the same
    /// call, and the useful answer is which channel <i>would</i> have carried it.
    /// </summary>
    public static string Refusal(ActionKind kind, string window) =>
        kind == ActionKind.ScrollIntoView
            ? $"后台模式：'{window}' 里这个控件没有 ScrollItem/ScrollPattern，没有不动前台的办法把它滚进视野"
            : $"后台模式：'{window}' 没有可用的页面调试端口，{kind} 只能靠 SendInput（要把窗口带到前台、动用户的指针），所以没有执行。"
              + "可以：① 点名控件用 elementId——走界面自动化，不需要前台（leftClick / focus / scrollIntoView，"
              + "scroll 和 typeText 在控件支持时也行）；"
              + "② 如果它是浏览器或 Electron 程序，"
              + "关掉后用 desktop_launch 重新启动，让它带上调试端口，之后坐标、按键、打字都在后台走页面协议；"
              + "③ 确实需要接管屏幕时，让用户把桌面输入模式改成 auto 或 foreground。";
}
