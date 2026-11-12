using Partner.Desktop.Input;

namespace Partner.Desktop.Tools;

/// <summary>
/// System-level key combinations that leave the window the model was approved to work in: Win+R opens a run
/// dialog, Win+L locks the session, Alt+F4 closes the application, Ctrl+Shift+Esc opens Task Manager. A task
/// lease over "continue working in this window" must not silently widen into those, so they are named here and
/// governance asks for each one separately.
/// <para>
/// Matching is a <b>superset</b> on modifiers: Alt+F4 with Shift also held is still Alt+F4, and every combination
/// that includes Win is blocked by the Win rule rather than by enumerating key names.
/// </para>
/// </summary>
public static class SystemShortcutGuard
{
    private const ushort VkTab = 0x09;
    private const ushort VkEscape = 0x1B;
    private const ushort VkDelete = 0x2E;
    private const ushort VkF4 = 0x73;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkRightWin = 0x5C;

    /// <summary>
    /// The refusal for this call, or null when it is not a blocked system combination. Parsing is tolerant: a
    /// malformed call is left to the tool, which reports the precise argument error.
    /// </summary>
    public static string? DescribeBlocked(string argumentsJson)
    {
        // Parse with the same options the tool itself uses: System.Text.Json's default is case-sensitive, so
        // {"Kind":"KeyPress","VirtualKey":82} would pass this guard and still execute as Win+R.
        var arguments = Json.Deserialize<DesktopActTool.DesktopActArguments>(argumentsJson);
        if (arguments?.Kind is not { Length: > 0 } kindName
            || !Enum.TryParse<ActionKind>(kindName, ignoreCase: true, out var kind)
            || kind != ActionKind.KeyPress
            || arguments.VirtualKey is not { } key
            || key == 0)
        {
            return null;
        }

        IReadOnlyList<ushort> modifiers = arguments.Modifiers is { Count: > 0 } names
            ? names
                .Select(name => name is { Length: > 0 }
                    && InputInjector.ModifierKeys.TryGetValue(name, out var code)
                        ? code
                        : (ushort)0)
                .Where(code => code != 0)
                .ToList()
            : Array.Empty<ushort>();

        return IsBlocked(key, modifiers) ? $"系统组合键 {Name(key, modifiers)}" : null;
    }

    /// <summary>Whether the combination is on the list. Exposed so the rule can be asserted without a host.</summary>
    public static bool IsBlocked(ushort virtualKey, IReadOnlyList<ushort> modifiers)
    {
        var has = (ushort wanted) => modifiers.Contains(wanted);

        // Any Win chord, and the Win key itself. Enumerating "Win+R" would miss Win+X, Win+S and the rest.
        if (virtualKey is VkLeftWin or VkRightWin || has(InputInjector.VirtualWin))
        {
            return true;
        }

        // Ctrl+Alt+anything: the classic escape hatch (Ctrl+Alt+Delete, Ctrl+Alt+Esc, vendor hotkeys).
        if (has(InputInjector.VirtualControl) && has(InputInjector.VirtualMenu))
        {
            return true;
        }

        // Alt+F4 closes the focused application; Alt+Tab leaves the approved window entirely.
        if (has(InputInjector.VirtualMenu) && (virtualKey is VkF4 or VkTab))
        {
            return true;
        }

        // Ctrl+Shift+Esc opens Task Manager.
        if (has(InputInjector.VirtualControl) && has(InputInjector.VirtualShift) && virtualKey == VkEscape)
        {
            return true;
        }

        return false;
    }

    /// <summary>The combination as a person writes it, for the approval banner and the refusal.</summary>
    public static string Name(ushort virtualKey, IReadOnlyList<ushort> modifiers)
    {
        var parts = new List<string>(modifiers.Count + 1);

        foreach (var modifier in modifiers.Distinct())
        {
            parts.Add(modifier switch
            {
                InputInjector.VirtualShift => "shift",
                InputInjector.VirtualControl => "ctrl",
                InputInjector.VirtualMenu => "alt",
                InputInjector.VirtualWin => "win",
                _ => modifier.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        parts.Add(virtualKey switch
        {
            VkTab => "tab",
            VkEscape => "esc",
            VkDelete => "delete",
            VkF4 => "f4",
            0x5B or 0x5C => "win",
            _ => char.ToLowerInvariant((char)virtualKey).ToString(),
        });

        return string.Join("+", parts);
    }
}
