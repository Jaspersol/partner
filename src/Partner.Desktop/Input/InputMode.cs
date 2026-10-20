namespace Partner.Desktop.Input;

/// <summary>
/// How an action may reach its target.
/// <para>
/// <c>SendInput</c> is one global queue: the pointer is the user's pointer and the keyboard goes to whoever
/// holds the foreground. So an action carried by it can only be made by taking the screen over — raising the
/// window, moving the cursor. The other channels (a page's debug protocol, a UI Automation pattern) address the
/// control itself and leave the user's windows alone. This says how far the agent may go to use the first one.
/// </para>
/// </summary>
public enum InputMode
{
    /// <summary>
    /// Everything as before: a channel that needs no foreground is used when the action names a control it
    /// supports, anything else takes the screen over. The default, because it is what every earlier session
    /// was tested against.
    /// </summary>
    Foreground,

    /// <summary>
    /// Channels that need no foreground first — including coordinate, key and text input into a Chromium
    /// window through its debug port — and the foreground only when nothing else can carry the action.
    /// </summary>
    Auto,

    /// <summary>
    /// Never take the screen over. An action no background channel can carry is refused, and the refusal says
    /// which channel would have carried it, so the model asks for that instead of retrying the same thing.
    /// </summary>
    Background,
}

/// <summary>The names an <see cref="InputMode"/> goes by in settings.</summary>
public static class InputModes
{
    public const string Foreground = "foreground";

    public const string Auto = "auto";

    public const string Background = "background";

    /// <summary>Whether the text names a mode. Settings validation uses this; <see cref="Parse"/> never throws.</summary>
    public static bool IsValid(string? value) =>
        value is Foreground or Auto or Background;

    /// <summary>
    /// The mode a setting names. An unknown or missing value is <see cref="InputMode.Foreground"/> — the
    /// behaviour that existed before the setting did — rather than an error: a settings file written by an
    /// older build must keep working.
    /// </summary>
    public static InputMode Parse(string? value) => value switch
    {
        Auto => InputMode.Auto,
        Background => InputMode.Background,
        _ => InputMode.Foreground,
    };

    public static string Name(InputMode mode) => mode switch
    {
        InputMode.Auto => Auto,
        InputMode.Background => Background,
        _ => Foreground,
    };
}
