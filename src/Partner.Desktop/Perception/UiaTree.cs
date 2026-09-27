using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Partner.Desktop.Perception;

/// <summary>
/// One addressable control inside a window: what the reference scheme calls a discovered entity.
/// </summary>
/// <param name="ElementId">Valid for one observation only — it is an index into that observation, not a handle.</param>
/// <param name="WindowId">The window it belongs to; acting on an element needs that window's lease.</param>
/// <param name="Name">The label a human reads ("播放", "发送", "文件名").</param>
/// <param name="Role">The control type ("Button", "Edit", …).</param>
/// <param name="Rect">Screen rectangle; the click point is its centre.</param>
/// <param name="Digest">Identity digest (name, role, rectangle) — deliberately not the value: typing into
/// a field must not make the field look like a different element.</param>
/// <param name="Value">What a text field holds, when UIA exposes it.</param>
public sealed record UiElement(
    string ElementId,
    string WindowId,
    string Name,
    string Role,
    PixelRect Rect,
    string Digest,
    string? Value = null)
{
    /// <summary>Where a click on this element should land.</summary>
    public (int X, int Y) Centre => (Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
}

/// <summary>
/// Reads the UI Automation tree. This is what makes "click the 播放 button" possible: the model addresses a
/// control by what it is, instead of guessing screen coordinates — the failure mode that once typed a letter
/// into whatever window happened to be focused.
/// <para>
/// UIA is per-process RPC, so the walk is bounded on purpose: a depth limit, a cap per window, and a timeout
/// per window. An app that is blind or slow (Chromium, games, canvases) yields nothing and the window-level
/// path still works.
/// </para>
/// </summary>
public static class UiaTree
{
    /// <summary>How deep to walk. Deep trees are mostly layout panes; the controls live near the top.</summary>
    private const int MaxDepth = 6;

    /// <summary>How many elements one window may contribute, so one app cannot fill the whole answer.</summary>
    private const int MaxElementsPerWindow = 40;

    /// <summary>How much of a document's text to report: enough to verify a change, not the whole file.</summary>
    private const int MaxValueChars = 200;

    /// <summary>Roles that are not actionable; walking into them costs time and returns nothing.</summary>
    private static readonly HashSet<string> Interesting = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "ListItem", "MenuItem", "TabItem",
        "Hyperlink", "Slider", "Spinner", "TreeItem", "DataItem", "Text", "SplitButton", "Thumb",
        // A document is the text surface of an editor; its value is how an agent verifies its own typing.
        "Document",
    };

    /// <summary>
    /// Interactive elements of one window, by native handle. Returns an empty list when UIA has nothing to
    /// say about it — never throws: a window that cannot be inspected is not an error, it is a window the
    /// coordinate path is for.
    /// </summary>
    public static IReadOnlyList<UiElement> Read(nint handle, string windowId)
    {
        var found = new List<UiElement>();

        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null)
            {
                return found;
            }

            Walk(root, windowId, depth: 0, found);
        }
        catch (ElementNotAvailableException)
        {
            // The window closed between enumerating it and reading it.
        }
        catch (COMException)
        {
            // A window that refuses inspection (elevated process, protected surface).
        }
        catch (TimeoutException)
        {
            // A busy app: its elements are not worth blocking the observation for.
        }

        return found;
    }

    private static void Walk(AutomationElement node, string windowId, int depth, List<UiElement> found)
    {
        if (depth > MaxDepth || found.Count >= MaxElementsPerWindow)
        {
            return;
        }

        AutomationElement? child;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(node);
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException)
        {
            return;
        }

        while (child is not null && found.Count < MaxElementsPerWindow)
        {
            var element = Describe(child, windowId);
            if (element is not null)
            {
                found.Add(element);
            }

            Walk(child, windowId, depth + 1, found);

            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch (Exception error) when (error is ElementNotAvailableException or COMException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A text field's content, when it exposes one. The value is reported but never part of the identity digest:
    /// a field whose text changed is the same field.
    /// </summary>
    private static string? ReadValue(AutomationElement node)
    {
        try
        {
            if (node.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
            {
                return value.Current.Value;
            }

            // Editors (RichEdit, and anything document-shaped) expose their text through TextPattern instead.
            if (node.TryGetCurrentPattern(TextPattern.Pattern, out var text) && text is TextPattern document)
            {
                var content = document.DocumentRange.GetText(MaxValueChars);
                return content.Length == 0 ? null : content;
            }

            return null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The element as the model sees it, or null when it is not something to act on.</summary>
    private static UiElement? Describe(AutomationElement node, string windowId)
    {
        AutomationElement.AutomationElementInformation current;
        try
        {
            current = node.Current;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException)
        {
            return null;
        }

        var role = current.ControlType?.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)
            ?? "Unknown";

        var name = current.Name ?? string.Empty;

        // An unnamed element is only worth reporting when it is a thing you can press; a nameless pane is
        // layout, and layout is noise.
        var actionable = Interesting.Contains(role);
        if (!actionable || (name.Length == 0 && role is not ("Button" or "ListItem" or "MenuItem")))
        {
            return null;
        }

        if (!current.IsEnabled || current.IsOffscreen)
        {
            return null;
        }

        var bounds = current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2)
        {
            return null;
        }

        var rect = new PixelRect(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height));

        return new UiElement(
            ElementId: string.Empty, // assigned by the observation, which knows the window's position in it
            WindowId: windowId,
            Name: name,
            Role: role,
            Rect: rect,
            Digest: DesktopObservation.Digest(name, role, rect),
            Value: ReadValue(node));
    }
}
