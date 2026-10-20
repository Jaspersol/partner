using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Partner.Desktop.Input;

namespace Partner.Desktop.Perception;

/// <summary>
/// One addressable control inside a window.
/// </summary>
/// <param name="ElementId">Valid for one observation only; it is an index into that observation, not a handle.</param>
/// <param name="WindowId">The window it belongs to; acting on an element needs that window's lease.</param>
/// <param name="Name">The label a human reads.</param>
/// <param name="Role">The control type ("Button", "Edit", …).</param>
/// <param name="Rect">Screen rectangle; the click point is its centre.</param>
/// <param name="IdentityDigest">Identity digest (window, name, role), deliberately not the value and not the
/// rectangle: typing into a field must not make it a different element, and neither must a layout shift. The
/// click point is recomputed from the fresh rectangle at act time.</param>
/// <param name="Value">What a text field holds, when UIA exposes it.</param>
/// <param name="IsOffscreen">
/// Whether the control is outside the window's visible area (below the fold of a list, collapsed into a tree).
/// Kept in the answer rather than filtered out, which is what makes <c>scrollIntoView</c> possible: an element
/// the walk had thrown away could never be scrolled to. Its click point is outside the window, so an action on
/// one is refused until it has been scrolled to.
/// </param>
public sealed record UiElement(
    string ElementId,
    string WindowId,
    string Name,
    string Role,
    PixelRect Rect,
    string IdentityDigest,
    string? Value = null,
    bool IsOffscreen = false)
{
    /// <summary>Where a click on this element should land.</summary>
    public (int X, int Y) Centre => (Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
}

/// <summary>
/// What happened when a control was acted on through a UI Automation pattern instead of injected input.
/// Distinct from an <c>InjectionResult</c>: no input event was sent, and reporting it as one would make the log
/// claim the user's own keyboard and mouse were used.
/// </summary>
/// <param name="Ok">Whether the pattern call went through.</param>
/// <param name="Method">The pattern used, e.g. <c>uia.invoke</c> — carried into the result so the model can
/// tell a pattern call from a coordinate click.</param>
/// <param name="Detail">Human-readable outcome, including the failure kind.</param>
public sealed record UiaInvocation(bool Ok, string Method, string Detail);

/// <summary>
/// What a UI Automation control can be asked to do to itself, with no pointer and no scrolling guess. These are
/// the platform's own answers to "make this reachable"; recomputing either here would be a guess about someone
/// else's layout.
/// </summary>
public static class UiaScroll
{
    /// <summary>
    /// Brings a control into view through <c>ScrollItemPattern</c>, falling back to <c>ScrollPattern</c> for a
    /// container that scrolls as a whole. Null when the control offers neither; the only thing left then is
    /// scrolling a container by coordinates, which is the guess this exists to avoid.
    /// </summary>
    public static UiaInvocation? ScrollIntoView(AutomationElement node)
    {
        try
        {
            if (node.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var item) && item is ScrollItemPattern scrollItem)
            {
                scrollItem.ScrollIntoView();
                return new UiaInvocation(true, "uia.scrollintoview", "已用 ScrollItemPattern 把该控件滚入视野");
            }

            if (node.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && pattern is ScrollPattern scroll)
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                return new UiaInvocation(true, "uia.scroll", "已用 ScrollPattern 滚动容器");
            }

            return null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.scrollintoview", $"滚动入视野失败：{error.GetType().Name}");
        }
    }

    /// <summary>How many ancestors are searched for something that scrolls. A scrollable list is a few levels up
    /// from a row; a search that climbs to the desktop would scroll something the model did not name.</summary>
    private const int MaxAncestors = 8;

    /// <summary>One wheel notch, in the small steps <c>ScrollPattern</c> offers (Windows' own default is three lines).</summary>
    private const int StepsPerNotch = 3;

    /// <summary>The most small steps one call makes. Each is a cross-process call, and a runaway delta is a typo.</summary>
    private const int MaxSteps = 60;

    /// <summary>
    /// Scrolls the control, or the nearest scrollable container above it, by wheel notches (positive is up). The
    /// pattern call reaches the container itself, so it needs neither the foreground nor the pointer over it.
    /// Null when nothing in reach scrolls: a reason to try another channel, not a failure.
    /// </summary>
    public static UiaInvocation? ScrollBy(AutomationElement node, int notches)
    {
        if (notches == 0)
        {
            return null;
        }

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var current = node;

            for (var depth = 0; depth <= MaxAncestors && current is not null; depth++)
            {
                if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out var found)
                    && found is ScrollPattern scroll
                    && scroll.Current.VerticallyScrollable)
                {
                    var amount = notches > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
                    var steps = Math.Min(MaxSteps, Math.Abs(notches) * StepsPerNotch);

                    for (var step = 0; step < steps; step++)
                    {
                        scroll.Scroll(ScrollAmount.NoAmount, amount);
                    }

                    return new UiaInvocation(
                        true,
                        "uia.scrollby",
                        $"已用 ScrollPattern 滚动{(depth == 0 ? "该控件" : "它上层的可滚动容器")}（后台：不需要前台，也不动用户的指针）");
                }

                current = walker.GetParent(current);
            }

            return null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.scrollby", $"滚动失败：{error.GetType().Name}");
        }
    }

    /// <summary>
    /// Types into a field by writing its value, for a field that offers a value pattern.
    /// <c>ValuePattern.SetValue</c> replaces everything, and typing must not wipe a half-filled form, so what is
    /// written is the existing text plus the new text. The pattern cannot say where the caret is, so this is
    /// "type at the end" and the result says so. Null for a field with no value pattern or a read-only one; the
    /// caller then tries another channel.
    /// </summary>
    public static UiaInvocation? AppendText(AutomationElement node, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            if (!node.TryGetCurrentPattern(ValuePattern.Pattern, out var found)
                || found is not ValuePattern value
                || value.Current.IsReadOnly)
            {
                return null;
            }

            var existing = value.Current.Value ?? string.Empty;
            value.SetValue(existing + text);

            return new UiaInvocation(
                true,
                "uia.value",
                existing.Length == 0
                    ? $"已用 ValuePattern 写入 {text.Length} 个字符（字段原本为空；后台：不需要前台）"
                    : $"已用 ValuePattern 把 {text.Length} 个字符追加到原有 {existing.Length} 个字符之后"
                      + "（后台下无法知道光标在哪，所以只能加在末尾）");
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.value", $"ValuePattern 写入失败：{error.GetType().Name}");
        }
    }

    /// <summary>Expands a collapsed node — a tree item, a disclosure — so its children can be seen at all.
    public static UiaInvocation? Expand(AutomationElement node)
    {
        try
        {
            if (!node.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)
                || pattern is not ExpandCollapsePattern expand)
            {
                return null;
            }

            expand.Expand();
            return new UiaInvocation(true, "uia.expand", "已用 ExpandCollapsePattern 展开该控件");
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.expand", $"展开失败：{error.GetType().Name}");
        }
    }
}

/// <summary>
/// Reads the UI Automation tree, which is what makes "click the play button" possible: the model addresses a
/// control by what it is instead of guessing screen coordinates.
/// <para>
/// UIA is per-process RPC, so the walk is bounded on purpose: depth, controls per window, nodes visited and a
/// per-window time budget; see <see cref="WalkBudget"/>. An app that is blind or slow (Chromium, games, canvases)
/// yields nothing and the window-level path still works.
/// </para>
/// </summary>
public static class UiaTree
{
    /// <summary>How much of a document's text to report: enough to verify a change, not the whole file.</summary>
    private const int MaxValueChars = 200;

    /// <summary>
    /// Roles that are actionable; walking into the rest costs time and returns nothing. <c>Text</c> is
    /// deliberately excluded: static labels are the most numerous element in a real interface, and including
    /// them let labels consume the 40-control budget meant for controls.
    /// </summary>
    private static readonly HashSet<string> Interesting = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "ListItem", "MenuItem", "TabItem",
        "Hyperlink", "Slider", "Spinner", "TreeItem", "DataItem", "SplitButton", "Thumb",
        // A document is the text surface of an editor; its value is how an agent verifies its own typing.
        "Document",
    };

    /// <summary>
    /// Roles whose text is worth reading. The value is one cross-process RPC per control, and
    /// <c>TextPattern.DocumentRange.GetText</c> pulls a document range to answer, while only text the user typed
    /// is meaningful. Static <c>Text</c> controls are the most numerous elements, so reading all of them was the
    /// single largest cost in an observation.
    /// </summary>
    private static readonly HashSet<string> ValueBearing = new(StringComparer.Ordinal)
    {
        "Edit", "Document", "ComboBox",
    };

    /// <summary>
    /// Whether this role's text is read. Exposed so the rule can be asserted without a window.
    /// </summary>
    internal static bool ReadsValue(string role) => ValueBearing.Contains(role);

    /// <summary>
    /// Interactive elements of one window, by native handle. Returns an empty list when UIA has nothing to say
    /// about it; never throws, since a window that cannot be inspected is a window the coordinate path is for.
    /// </summary>
    public static IReadOnlyList<UiElement> Read(nint handle, string windowId) => WalkWindow(handle, windowId).Elements;

    /// <summary>
    /// The same walk, with each control numbered the way an observation numbers it. Numbering is by position
    /// within the window, which makes an element id mean "this control, as of the last read", so it must be
    /// produced in exactly one place. The default budget matches the one <see cref="TryAct"/> walks with: an id
    /// is an index into this list, so a different budget would name a different control.
    /// </summary>
    public static IReadOnlyList<UiElement> ReadNumbered(nint handle, string windowId) =>
        Number(WalkWindow(handle, windowId, WalkBudget.Default).Elements, windowId);

    /// <summary>
    /// The same walk, keeping the live controls behind the numbered elements. An action needs both halves: the
    /// numbering says which control the model meant, and the <see cref="AutomationElement"/> is what a pattern is
    /// invoked on. Returning them together avoids a second walk, which is the expensive half of pressing a
    /// control by id because UI Automation is a cross-process RPC per node.
    /// </summary>
    internal static (IReadOnlyList<UiElement> Elements, IReadOnlyList<AutomationElement> Nodes, WalkStopReason Stopped)
        ReadNumberedWithNodes(
            nint handle,
            string windowId)
    {
        var (elements, nodes, stopped) = WalkWindow(handle, windowId, WalkBudget.Default);
        return (Number(elements, windowId), nodes, stopped);
    }

    private static List<UiElement> Number(IReadOnlyList<UiElement> elements, string windowId)
    {
        var numbered = new List<UiElement>(elements.Count);

        for (var index = 0; index < elements.Count; index++)
        {
            numbered.Add(elements[index] with { ElementId = $"{windowId}:{index}" });
        }

        return numbered;
    }

    /// <summary>
    /// One walk of a window's control view, in two lockstep lists: what the model was shown, and the live
    /// <see cref="AutomationElement"/> behind each entry. An id is an index into this list, so re-walking
    /// recovers the control that index pointed at, with no name search that could match another element.
    /// </summary>
    private static (IReadOnlyList<UiElement> Elements, IReadOnlyList<AutomationElement> Nodes, WalkStopReason Stopped)
        WalkWindow(
            nint handle,
            string windowId,
            WalkBudget? budget = null)
    {
        var elements = new List<UiElement>();
        var nodes = new List<AutomationElement>();
        var state = new WalkState(budget ?? WalkBudget.Default, Stopwatch.StartNew());

        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null)
            {
                return (elements, nodes, WalkStopReason.None);
            }

            Walk(root, windowId, depth: 0, elements, nodes, state);
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
            // A busy app: its elements are not worth blocking the observation for. The per-window budget above
            // is the deliberate version of this: it stops the walk where we choose rather than wherever the
            // provider's internal timeout lands.
        }

        return (elements, nodes, state.Stopped);
    }

    private static void Walk(
        AutomationElement node,
        string windowId,
        int depth,
        List<UiElement> elements,
        List<AutomationElement> nodes,
        WalkState state)
    {
        if (!state.Allows(depth, elements.Count))
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

        while (child is not null)
        {
            // Checked per node rather than per produced element: a produced-only cap is never reached by a
            // window made entirely of skipped controls, so such a window would be walked to its last node,
            // which is exactly the shape a Chromium window has.
            if (!state.Allows(depth, elements.Count))
            {
                return;
            }

            state.Visit();

            var element = Describe(child, windowId);
            if (element is not null)
            {
                elements.Add(element);
                nodes.Add(child);
            }

            Walk(child, windowId, depth + 1, elements, nodes, state);

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
    /// <param name="node">The control to read.</param>
    /// <param name="role">
    /// Its control type, and the reason this is not called for every control: the read is a cross-process RPC
    /// and <c>TextPattern.DocumentRange.GetText</c> pulls a whole document range to answer it.
    /// </param>
    private static string? ReadValue(AutomationElement node, string role)
    {
        if (!ReadsValue(role))
        {
            return null;
        }

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

    /// <summary>
    /// What distinguishes this control from every other one of the same name and kind: its automation id, class
    /// and runtime id. The runtime id is unique among live controls and lasts as long as the control does, which
    /// is the span between an observation and the action on it. Null when none can be read.
    /// </summary>
    private static string? StableKey(AutomationElement node, AutomationElement.AutomationElementInformation current)
    {
        string runtime;
        try
        {
            runtime = string.Join('.', node.GetRuntimeId());
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            runtime = string.Empty;
        }

        var automationId = current.AutomationId ?? string.Empty;
        var className = current.ClassName ?? string.Empty;

        return automationId.Length == 0 && className.Length == 0 && runtime.Length == 0
            ? null
            : $"{automationId}|{className}|{runtime}";
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

        // A disabled control is dropped: there is no action that means "press the greyed-out button", and
        // offering one would only produce a refusal after a round trip. An offscreen one is kept — that is the
        // difference between an element something can scroll to and one that never appears at all.
        if (!current.IsEnabled)
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
            IdentityDigest: DesktopObservation.ElementIdentity(windowId, name, role, StableKey(node, current)),
            Value: ReadValue(node, role),
            IsOffscreen: current.IsOffscreen);
    }

    /// <summary>The live control behind an element id, when the walk that numbered it is still the one in hand.</summary>
    public static AutomationElement? NodeOf(
        string windowId,
        string elementId,
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<AutomationElement> Nodes) known)
    {
        var index = ParseIndex(windowId, elementId);
        return index >= 0 && index < known.Nodes.Count ? known.Nodes[index] : null;
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
    /// Acts on a named control through a UI Automation pattern instead of a coordinate click, when the control
    /// supports one.
    /// <para>
    /// A pattern call reaches the control itself. It needs no foreground window, so it cannot land in whatever
    /// the user is looking at, and it is unaffected by the control moving since the observation. It is also the
    /// only way to press an obscured control.
    /// </para>
    /// Returns null when this does not apply: the control is gone, it exposes none of the patterns that mean
    /// "click", or the action is one only a real input device can perform. The caller then injects input at the
    /// element's centre.
    /// </summary>
    /// <param name="kind">
    /// Pattern-shaped: <see cref="ActionKind.LeftClick"/> and <see cref="ActionKind.Focus"/> press a control,
    /// <see cref="ActionKind.ScrollIntoView"/> makes it reachable. <c>TypeText</c> is excluded by default:
    /// <c>ValuePattern.SetValue</c> replaces a field's entire contents, and silently wiping a half-filled form is
    /// not what "type this" means. A double click is excluded too: there is no pattern for "press twice in time",
    /// and sending <c>Invoke</c> twice is not it. With <paramref name="background"/>, <c>TypeText</c> becomes an
    /// append and <see cref="ActionKind.Scroll"/> becomes a <c>ScrollPattern</c> call.
    /// </param>
    /// <param name="parameters">The action's amounts and text; needed only by the two background kinds.</param>
    /// <param name="background">Whether the caller has agreed to patterns standing in for wheel and typing.</param>
    /// <param name="known">
    /// The walk this action was decided on, when the caller already has it. Re-walking is what turns an id back
    /// into a live control and doubles as an "is it still there" check; the caller's re-observation is fresher,
    /// so passing it saves a second traversal.
    /// <para>
    /// The two lists must be the lockstep pair one walk produced. A mismatched pair would name a different
    /// control than the one its number was assigned to, which is the whole reason an id is an index.
    /// </para>
    /// </param>
    public static UiaInvocation? TryAct(
        nint handle,
        string windowId,
        string elementId,
        ActionKind kind,
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<AutomationElement> Nodes)? known = null,
        ActionParams? parameters = null,
        bool background = false)
    {
        // Wheel and typing are patterns only when the caller has agreed to the trade: ScrollPattern moves a
        // container rather than turning the wheel over it, and ValuePattern appends rather than types. Where
        // the foreground is available a real wheel and real keystrokes are the more faithful action, so that
        // stays the default.
        var backgroundKind = background && kind is ActionKind.Scroll or ActionKind.TypeText;

        if (kind is not (ActionKind.LeftClick or ActionKind.Focus or ActionKind.ScrollIntoView) && !backgroundKind)
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
        IReadOnlyList<UiElement> elements;
        IReadOnlyList<AutomationElement> nodes;

        if (known is { } pair)
        {
            (elements, nodes) = (pair.Elements, pair.Nodes);
        }
        else
        {
            var found = WalkWindow(handle, windowId);
            elements = found.Elements;
            nodes = found.Nodes;
        }

        if (index >= nodes.Count)
        {
            return null;
        }

        var node = nodes[index];

        if (kind == ActionKind.Focus)
        {
            return TrySetFocus(node);
        }

        if (kind == ActionKind.Scroll)
        {
            return UiaScroll.ScrollBy(node, parameters?.ScrollDelta ?? 0);
        }

        if (kind == ActionKind.TypeText)
        {
            return UiaScroll.AppendText(node, parameters?.Text);
        }

        if (kind == ActionKind.ScrollIntoView)
        {
            // The control knows how far it is from the viewport; scrolling by estimate is the guess this avoids.
            return UiaScroll.ScrollIntoView(node) ?? UiaScroll.Expand(node);
        }

        // Which pattern means "click" depends on what the control is: pressing a list row selects it rather
        // than invoking it, and pressing a checkbox flips it. The wrong pattern is a different action.
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
