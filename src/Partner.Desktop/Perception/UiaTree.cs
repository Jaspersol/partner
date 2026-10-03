using System.Runtime.InteropServices;
using System.Windows.Automation;
using Partner.Desktop.Input;

namespace Partner.Desktop.Perception;

/// <summary>
/// One addressable control inside a window: what the reference scheme calls a discovered entity.
/// </summary>
/// <param name="ElementId">Valid for one observation only — it is an index into that observation, not a handle.</param>
/// <param name="WindowId">The window it belongs to; acting on an element needs that window's lease.</param>
/// <param name="Name">The label a human reads ("播放", "发送", "文件名").</param>
/// <param name="Role">The control type ("Button", "Edit", …).</param>
/// <param name="Rect">Screen rectangle; the click point is its centre.</param>
/// <param name="IdentityDigest">Identity digest (window, name, role) — deliberately not the value, and
/// deliberately not the rectangle: typing into a field must not make the field look like a different element,
/// and a layout shift must not make it look like a different one either. The click point is recomputed from
/// the fresh rectangle at act time, so drift is already handled where it matters.</param>
/// <param name="Value">What a text field holds, when UIA exposes it.</param>
public sealed record UiElement(
    string ElementId,
    string WindowId,
    string Name,
    string Role,
    PixelRect Rect,
    string IdentityDigest,
    string? Value = null)
{
    /// <summary>Where a click on this element should land.</summary>
    public (int X, int Y) Centre => (Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
}

/// <summary>
/// What happened when a control was acted on through a UI Automation pattern instead of injected input.
/// Distinct from an <c>InjectionResult</c> on purpose: no input event was sent, and reporting it as one
/// would make the log claim the user's own keyboard and mouse were used.
/// </summary>
/// <param name="Ok">Whether the pattern call went through.</param>
/// <param name="Method">The pattern used, e.g. <c>uia.invoke</c> — carried into the result so the model can
/// tell a pattern call from a coordinate click.</param>
/// <param name="Detail">Human-readable outcome, including the failure kind.</param>
public sealed record UiaInvocation(bool Ok, string Method, string Detail);

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
    public static IReadOnlyList<UiElement> Read(nint handle, string windowId) => WalkWindow(handle, windowId).Elements;

    /// <summary>
    /// The same walk, with each control numbered the way an observation numbers it.
    /// <para>
    /// The numbering is by position within the window, which is what makes an element id mean "this control,
    /// as of the last read" — and it is why it has to be produced in exactly one place. A screenshot that
    /// renumbered independently would put different ids on the same marks than the observation the model is
    /// holding, and clicking a mark would then act on whatever now sits at that index.
    /// </para>
    /// </summary>
    public static IReadOnlyList<UiElement> ReadNumbered(nint handle, string windowId)
    {
        var elements = Read(handle, windowId);
        var numbered = new List<UiElement>(elements.Count);

        for (var index = 0; index < elements.Count; index++)
        {
            numbered.Add(elements[index] with { ElementId = $"{windowId}:{index}" });
        }

        return numbered;
    }

    /// <summary>
    /// One walk of a window's control view, kept in two lockstep lists: what the model was shown, and the
    /// live <see cref="AutomationElement"/> behind each entry.
    /// <para>
    /// The pairing is what makes an element id actionable later. An id is an index into <i>this</i> list, so
    /// re-walking a window recovers the control that index pointed at — no name search that could match a
    /// different element that happens to share a label.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<UiElement> Elements, IReadOnlyList<AutomationElement> Nodes) WalkWindow(
        nint handle,
        string windowId)
    {
        var elements = new List<UiElement>();
        var nodes = new List<AutomationElement>();

        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null)
            {
                return (elements, nodes);
            }

            Walk(root, windowId, depth: 0, elements, nodes);
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

        return (elements, nodes);
    }

    private static void Walk(
        AutomationElement node,
        string windowId,
        int depth,
        List<UiElement> elements,
        List<AutomationElement> nodes)
    {
        if (depth > MaxDepth || elements.Count >= MaxElementsPerWindow)
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

        while (child is not null && elements.Count < MaxElementsPerWindow)
        {
            var element = Describe(child, windowId);
            if (element is not null)
            {
                elements.Add(element);
                nodes.Add(child);
            }

            Walk(child, windowId, depth + 1, elements, nodes);

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
            IdentityDigest: DesktopObservation.ElementIdentity(windowId, name, role),
            Value: ReadValue(node));
    }

    /// <summary>Index of an element id inside its window's element list, or -1 when it is not one.</summary>
    private static int ParseIndex(string windowId, string elementId)
    {
        var prefix = windowId + ":";
        return elementId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(elementId.AsSpan(prefix.Length), out var index)
            && index >= 0
                ? index
                : -1;
    }

    /// <summary>
    /// Acts on a named control through a UI Automation pattern instead of a coordinate click, when the
    /// control supports one.
    /// <para>
    /// A pattern call reaches the control itself. It needs no foreground window — so it cannot land in
    /// whatever the user is actually looking at — and it is unaffected by the control having moved a few
    /// pixels since the observation. It is also the only way to press a control that is obscured.
    /// </para>
    /// Returns null when this does not apply: the control is gone, it exposes none of the patterns that mean
    /// "click", or the action is one only a real input device can perform. The caller then injects input at
    /// the element's centre, which is what a person would do.
    /// </summary>
    /// <param name="kind">
    /// Only <see cref="ActionKind.LeftClick"/> and <see cref="ActionKind.Focus"/> are pattern-shaped.
    /// <c>TypeText</c> is deliberately excluded: <c>ValuePattern.SetValue</c> replaces a field's entire
    /// contents, and silently wiping a half-filled form is not what "type this" means.
    /// </param>
    public static UiaInvocation? TryAct(nint handle, string windowId, string elementId, ActionKind kind)
    {
        if (kind is not (ActionKind.LeftClick or ActionKind.Focus))
        {
            return null;
        }

        var index = ParseIndex(windowId, elementId);
        if (index < 0)
        {
            return null;
        }

        // Re-walking is what turns the id back into a live control, and it doubles as a "is it still there"
        // check: an index past the end means the window's tree shrank since the observation.
        var (elements, nodes) = WalkWindow(handle, windowId);
        if (index >= nodes.Count)
        {
            return null;
        }

        var node = nodes[index];

        if (kind == ActionKind.Focus)
        {
            return TrySetFocus(node);
        }

        // Which pattern means "click" depends on what the control is: pressing a list row selects it rather
        // than invoking it, and pressing a checkbox flips it. Getting this wrong is how a click turns into a
        // different action than the one the model asked for.
        return elements[index].Role switch
        {
            "CheckBox" => TryToggle(node) ?? TryInvoke(node),
            "RadioButton" or "ListItem" or "TabItem" or "DataItem" or "TreeItem" =>
                TrySelect(node) ?? TryInvoke(node),
            _ => TryInvoke(node) ?? TrySelect(node),
        };
    }

    /// <summary>Presses the control. Null when it cannot be pressed this way.</summary>
    private static UiaInvocation? TryInvoke(AutomationElement node)
    {
        try
        {
            if (!node.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                || pattern is not InvokePattern invoke)
            {
                return null;
            }

            invoke.Invoke();
            return new UiaInvocation(true, "uia.invoke", "已用 InvokePattern 触发（不需要前台窗口）");
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.invoke", $"InvokePattern 调用失败：{error.GetType().Name}");
        }
    }

    /// <summary>Selects the control — what a click does to a list row, tab or tree item.</summary>
    private static UiaInvocation? TrySelect(AutomationElement node)
    {
        try
        {
            if (!node.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                || pattern is not SelectionItemPattern select)
            {
                return null;
            }

            select.Select();
            return new UiaInvocation(true, "uia.select", "已用 SelectionItemPattern 选中（不需要前台窗口）");
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.select", $"SelectionItemPattern 调用失败：{error.GetType().Name}");
        }
    }

    /// <summary>Flips the control — what a click does to a checkbox.</summary>
    private static UiaInvocation? TryToggle(AutomationElement node)
    {
        try
        {
            if (!node.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                || pattern is not TogglePattern toggle)
            {
                return null;
            }

            toggle.Toggle();
            return new UiaInvocation(true, "uia.toggle", "已用 TogglePattern 切换（不需要前台窗口）");
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.toggle", $"TogglePattern 调用失败：{error.GetType().Name}");
        }
    }

    /// <summary>Gives the control keyboard focus without raising the window over the user's work.</summary>
    private static UiaInvocation? TrySetFocus(AutomationElement node)
    {
        try
        {
            node.SetFocus();
            return new UiaInvocation(true, "uia.setfocus", "已用 SetFocus 聚焦该控件");
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.setfocus", $"SetFocus 调用失败：{error.GetType().Name}");
        }
    }
}
