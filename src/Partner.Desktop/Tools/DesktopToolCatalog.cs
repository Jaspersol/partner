namespace Partner.Desktop.Tools;

/// <summary>
/// What each desktop tool can do to the machine, so the approval gear has something to key off.
/// <para>
/// "Dangerous" is about **leaving the GUI**: running a command, starting a program, writing the clipboard.
/// Clicking and typing stay in the interactive world where a person can see them happen — which is why they
/// are the ones the middle gear lets through, and why the reference project's example of a dangerous action
/// is deleting a file rather than pressing a button.
/// </para>
/// </summary>
public static class DesktopToolCatalog
{
    /// <summary>Tools that only look. They are never gated: asking for permission to read the screen would
    /// make the agent unusable and protect nothing.</summary>
    private static readonly HashSet<string> ReadOnlyNames = new(StringComparer.Ordinal)
    {
        "desktop_observe",
        "desktop_screenshot",
        "desktop_clipboard_get",
        "desktop_wait",
    };

    /// <summary>Tools that can affect the machine outside the window the model is looking at.</summary>
    private static readonly HashSet<string> DangerousNames = new(StringComparer.Ordinal)
    {
        "desktop_exec",
        "desktop_launch",
        "desktop_clipboard_set",
    };

    /// <summary>Every desktop tool, whether dangerous or not.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "desktop_observe",
        "desktop_screenshot",
        "desktop_act",
        "desktop_window",
        "desktop_wait",
        "desktop_launch",
        "desktop_exec",
        "desktop_clipboard_get",
        "desktop_clipboard_set",
    };

    public static bool IsReadOnly(string toolName) => ReadOnlyNames.Contains(toolName);

    public static bool IsDangerous(string toolName) => DangerousNames.Contains(toolName);
}
