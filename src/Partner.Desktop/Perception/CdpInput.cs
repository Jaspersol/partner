using Partner.Desktop.Input;

namespace Partner.Desktop.Perception;

/// <summary>One DevTools Protocol command, with the pause the sequence needs after it.</summary>
/// <param name="Method">e.g. <c>Input.dispatchMouseEvent</c>.</param>
/// <param name="Params">Serialised as the command's <c>params</c>.</param>
/// <param name="DelayMs">Waited after the command is acknowledged — the pacing a drag needs between moves.</param>
internal sealed record CdpCommand(string Method, object Params, int DelayMs = 0);

/// <summary>A key as the protocol names it.</summary>
/// <param name="Key">The <c>key</c> value (what the key means).</param>
/// <param name="Code">The <c>code</c> value (which physical key it is).</param>
/// <param name="VirtualKey">Windows virtual-key code; Chromium's own handling is keyed on it.</param>
/// <param name="Text">What the key types, or null when it types nothing (arrows, function keys).</param>
internal sealed record CdpKey(string Key, string Code, int VirtualKey, string? Text);

/// <summary>
/// Input for a page, as DevTools Protocol commands.
/// <para>
/// This channel makes Chromium and Electron windows workable in the background: events are delivered to the page
/// itself, so they need neither the foreground nor the pointer, and a covered, minimized or off-screen window
/// receives them like a visible one. Everything here is pure; sending the commands is
/// <see cref="CdpDomSource"/>'s job.
/// </para>
/// </summary>
internal static class CdpInput
{
    /// <summary>CSS pixels one wheel notch scrolls. Chromium's own notch is 100 px at 100% zoom.</summary>
    internal const double WheelNotchPixels = 100;

    // The protocol's modifier bitmask.
    private const int Alt = 1;
    private const int Ctrl = 2;
    private const int Meta = 4;
    private const int Shift = 8;

    // The buttons bitmask carried by events while a button is held.
    private const int LeftHeld = 1;
    private const int RightHeld = 2;

    /// <summary>Which modifier keys are held, as the protocol's bitmask. An unknown virtual key adds nothing.</summary>
    internal static int ModifierMask(IReadOnlyList<ushort> modifiers)
    {
        var mask = 0;
        foreach (var key in modifiers)
        {
            mask |= key switch
            {
                InputInjector.VirtualMenu => Alt,
                InputInjector.VirtualControl => Ctrl,
                InputInjector.VirtualWin => Meta,
                InputInjector.VirtualShift => Shift,
                _ => 0,
            };
        }

        return mask;
    }

    /// <summary>The protocol's name for a modifier key, so it can be pressed around the main key.</summary>
    private static CdpKey? ModifierKey(ushort virtualKey) => virtualKey switch
    {
        InputInjector.VirtualShift => new CdpKey("Shift", "ShiftLeft", 0x10, null),
        InputInjector.VirtualControl => new CdpKey("Control", "ControlLeft", 0x11, null),
        InputInjector.VirtualMenu => new CdpKey("Alt", "AltLeft", 0x12, null),
        InputInjector.VirtualWin => new CdpKey("Meta", "MetaLeft", 0x5B, null),
        _ => null,
    };

    /// <summary>
    /// The key a Windows virtual-key code means, or null for one this does not know. Null is a refusal, not a
    /// guess: sending a key the protocol cannot name would type something other than what was asked.
    /// </summary>
    internal static CdpKey? KeyOf(ushort virtualKey, bool shifted = false)
    {
        switch (virtualKey)
        {
            case 0x08: return new CdpKey("Backspace", "Backspace", 0x08, null);
            case 0x09: return new CdpKey("Tab", "Tab", 0x09, null);
            case 0x0D: return new CdpKey("Enter", "Enter", 0x0D, "\r");
            case 0x1B: return new CdpKey("Escape", "Escape", 0x1B, null);
            case 0x20: return new CdpKey(" ", "Space", 0x20, " ");
            case 0x21: return new CdpKey("PageUp", "PageUp", 0x21, null);
            case 0x22: return new CdpKey("PageDown", "PageDown", 0x22, null);
            case 0x23: return new CdpKey("End", "End", 0x23, null);
            case 0x24: return new CdpKey("Home", "Home", 0x24, null);
            case 0x25: return new CdpKey("ArrowLeft", "ArrowLeft", 0x25, null);
            case 0x26: return new CdpKey("ArrowUp", "ArrowUp", 0x26, null);
            case 0x27: return new CdpKey("ArrowRight", "ArrowRight", 0x27, null);
            case 0x28: return new CdpKey("ArrowDown", "ArrowDown", 0x28, null);
            case 0x2D: return new CdpKey("Insert", "Insert", 0x2D, null);
            case 0x2E: return new CdpKey("Delete", "Delete", 0x2E, null);
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            var digit = ((char)virtualKey).ToString();
            return new CdpKey(digit, $"Digit{digit}", virtualKey, digit);
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            var letter = ((char)virtualKey).ToString();
            var typed = shifted ? letter : letter.ToLowerInvariant();
            return new CdpKey(typed, $"Key{letter}", virtualKey, typed);
        }

        if (virtualKey is >= 0x70 and <= 0x7B)
        {
            var name = $"F{virtualKey - 0x6F}";
            return new CdpKey(name, name, virtualKey, null);
        }

        return null;
    }

    /// <summary>
    /// A key press with its modifiers held around it. A chord is modifiers down, key down and up, modifiers up,
    /// with the modifier bitmask on every event in between. A shortcut is sent as a raw key-down, because a
    /// key-down carrying text would also insert that text: Ctrl+A would type an "a".
    /// </summary>
    /// <returns>Null when a key in the chord has no protocol name.</returns>
    internal static IReadOnlyList<CdpCommand>? KeyCommands(ushort virtualKey, IReadOnlyList<ushort> modifiers)
    {
        var mask = ModifierMask(modifiers);
        var key = KeyOf(virtualKey, shifted: (mask & Shift) != 0);
        if (key is null)
        {
            return null;
        }

        var commands = new List<CdpCommand>();
        var held = 0;

        foreach (var modifier in modifiers)
        {
            if (ModifierKey(modifier) is not { } named)
            {
                return null;
            }

            held |= ModifierMask(new[] { modifier });
            commands.Add(KeyEvent("rawKeyDown", named, held, typesText: false));
        }

        // A shortcut types nothing, whatever the key would type on its own.
        var typesText = key.Text is not null && (mask & (Alt | Ctrl | Meta)) == 0;
        commands.Add(KeyEvent(typesText ? "keyDown" : "rawKeyDown", key, mask, typesText));
        commands.Add(KeyEvent("keyUp", key, mask, typesText: false));

        foreach (var modifier in modifiers.Reverse())
        {
            held &= ~ModifierMask(new[] { modifier });
            commands.Add(KeyEvent("keyUp", ModifierKey(modifier)!, held, typesText: false));
        }

        return commands;
    }

    private static CdpCommand KeyEvent(string type, CdpKey key, int modifiers, bool typesText)
    {
        var body = new Dictionary<string, object>
        {
            ["type"] = type,
            ["modifiers"] = modifiers,
            ["key"] = key.Key,
            ["code"] = key.Code,
            ["windowsVirtualKeyCode"] = key.VirtualKey,
            ["nativeVirtualKeyCode"] = key.VirtualKey,
        };

        if (typesText && key.Text is { } text)
        {
            body["text"] = text;
            body["unmodifiedText"] = text;
        }

        return new CdpCommand("Input.dispatchKeyEvent", body);
    }

    /// <summary>Text as the page receives it from an input method: whole, in one event, with no keyboard layout.</summary>
    internal static IReadOnlyList<CdpCommand> TextCommands(string text) =>
        new[] { new CdpCommand("Input.insertText", new { text }) };

    private static CdpCommand Mouse(string type, double x, double y, string button, int clickCount, int held, int modifiers = 0) =>
        new("Input.dispatchMouseEvent", new Dictionary<string, object>
        {
            ["type"] = type,
            ["x"] = Math.Round(x, 2),
            ["y"] = Math.Round(y, 2),
            ["button"] = button,
            ["buttons"] = held,
            ["clickCount"] = clickCount,
            ["modifiers"] = modifiers,
        });

    /// <summary>The pointer arriving at a point — what makes hover styles and tooltips happen.</summary>
    internal static IReadOnlyList<CdpCommand> MoveCommands(double x, double y) =>
        new[] { Mouse("mouseMoved", x, y, "none", 0, 0) };

    /// <summary>
    /// A click: the pointer arrives, then <paramref name="count"/> press/release pairs whose <c>clickCount</c>
    /// climbs, which is how the page tells a double click from two clicks.
    /// </summary>
    internal static IReadOnlyList<CdpCommand> ClickCommands(double x, double y, bool right, int count)
    {
        var button = right ? "right" : "left";
        var held = right ? RightHeld : LeftHeld;

        var commands = new List<CdpCommand> { Mouse("mouseMoved", x, y, "none", 0, 0) };
        for (var press = 1; press <= Math.Max(1, count); press++)
        {
            commands.Add(Mouse("mousePressed", x, y, button, press, held));
            commands.Add(Mouse("mouseReleased", x, y, button, press, 0));
        }

        return commands;
    }

    /// <summary>
    /// The wheel at a point. <paramref name="notches"/> follows the tool's convention (positive is up); the
    /// protocol's <c>deltaY</c> is the opposite (positive scrolls the page down), so the sign flips here once.
    /// </summary>
    internal static IReadOnlyList<CdpCommand> WheelCommands(double x, double y, int notches) =>
        new[]
        {
            Mouse("mouseMoved", x, y, "none", 0, 0),
            new CdpCommand("Input.dispatchMouseEvent", new Dictionary<string, object>
            {
                ["type"] = "mouseWheel",
                ["x"] = Math.Round(x, 2),
                ["y"] = Math.Round(y, 2),
                ["deltaX"] = 0,
                ["deltaY"] = -notches * WheelNotchPixels,
            }),
        };

    /// <summary>
    /// Press, travel, release, in steps with a pause between them. A drag that jumps from start to end in one
    /// move is a teleport; a page that tracks the pointer (a slider, a sortable list) never sees it move.
    /// </summary>
    internal static IReadOnlyList<CdpCommand> DragCommands(double fromX, double fromY, double toX, double toY)
    {
        const int Steps = 8;

        var commands = new List<CdpCommand>
        {
            Mouse("mouseMoved", fromX, fromY, "none", 0, 0),
            Mouse("mousePressed", fromX, fromY, "left", 1, LeftHeld),
        };

        for (var step = 1; step <= Steps; step++)
        {
            var progress = step / (double)Steps;
            commands.Add(new CdpCommand(
                "Input.dispatchMouseEvent",
                Mouse("mouseMoved", fromX + ((toX - fromX) * progress), fromY + ((toY - fromY) * progress), "left", 0, LeftHeld).Params,
                DelayMs: 15));
        }

        commands.Add(Mouse("mouseReleased", toX, toY, "left", 1, 0));
        return commands;
    }

    /// <summary>
    /// Where the page's viewport starts on the screen, from what the page says about itself. The client area is
    /// not the page: a browser draws its tab strip and address bar inside the client area above the page, while
    /// an Electron window has nothing there. The page's <c>innerWidth</c> and <c>innerHeight</c> say how much of
    /// the client area is page; the remainder is chrome, on top.
    /// <para>
    /// Null (and the caller refuses) when the numbers do not fit that picture: a width short by more than a
    /// window frame means a side panel this cannot place, and a page larger than its window means stale
    /// metrics. An approximate origin is a click on the neighbouring control.
    /// </para>
    /// </summary>
    internal static (int X, int Y)? ViewportOrigin(PixelRect client, double innerWidth, double innerHeight, double devicePixelRatio)
    {
        var scale = devicePixelRatio <= 0 ? 1 : devicePixelRatio;

        // Rounding of a scaled size can make the page a pixel or two off; more than that below zero is a page
        // larger than its window, which means the metrics are stale.
        const double Rounding = 3;

        // The most a window frame can take from each side: a Chromium window keeps its resize border inside the
        // client area (measured 4 px a side at 100%). Anything wider is not a frame, it is a panel.
        var widestBorder = 24 * scale;

        var extraWidth = client.Width - (innerWidth * scale);
        var extraHeight = client.Height - (innerHeight * scale);

        if (innerWidth <= 0 || innerHeight <= 0
            || extraWidth < -Rounding
            || extraWidth > 2 * widestBorder
            || extraHeight < -Rounding
            || extraHeight > 300 * scale)
        {
            return null;
        }

        // Side borders are equal, so half the leftover width is one border; the same thickness runs along the
        // bottom, so the page ends that much above the client area's end. The rest above the page is the
        // browser's toolbar.
        var border = Math.Max(0, extraWidth / 2);

        return (
            client.X + (int)Math.Round(border),
            client.Y + (int)Math.Round(Math.Max(extraHeight - border, 0)));
    }

    /// <summary>
    /// A screen point as the viewport CSS pixel the protocol takes, or null when the point is not on the page
    /// (it is on the window's chrome, or outside the window). Not clamped: a clamped point is a click somewhere
    /// the caller did not name.
    /// </summary>
    internal static (double X, double Y)? ToViewport(
        int screenX,
        int screenY,
        (int X, int Y) origin,
        double innerWidth,
        double innerHeight,
        double devicePixelRatio)
    {
        var scale = devicePixelRatio <= 0 ? 1 : devicePixelRatio;
        var x = (screenX - origin.X) / scale;
        var y = (screenY - origin.Y) / scale;

        return x >= 0 && y >= 0 && x < innerWidth && y < innerHeight ? (x, y) : null;
    }
}
