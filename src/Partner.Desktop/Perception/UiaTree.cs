using System.Diagnostics;
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
/// <param name="IsOffscreen">
/// Whether the control is outside the window's visible area — below the fold of a list, collapsed into a tree.
/// <para>
/// Kept in the answer rather than filtered out, and that is the whole reason <c>scrollIntoView</c> can work: an
/// element the walk had thrown away is an element nothing could ever scroll to, so "scroll down a bit and look
/// again" became a two-step dance the model had to get right by trial. Its click point is outside the window,
/// so an action on one is refused until it has been scrolled to.
/// </para>
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
/// Distinct from an <c>InjectionResult</c> on purpose: no input event was sent, and reporting it as one
/// would make the log claim the user's own keyboard and mouse were used.
/// </summary>
/// <param name="Ok">Whether the pattern call went through.</param>
/// <param name="Method">The pattern used, e.g. <c>uia.invoke</c> — carried into the result so the model can
/// tell a pattern call from a coordinate click.</param>
/// <param name="Detail">Human-readable outcome, including the failure kind.</param>
public sealed record UiaInvocation(bool Ok, string Method, string Detail);

/// <summary>
/// What a UI Automation control can be asked to do to itself, with no pointer and no scrolling guess.
/// <para>
/// These are the platform's own answers to "make this reachable": a list item below the fold says how far it
/// is from the viewport, and a collapsed node says it has one to expand. Recomputing either here would be a
/// guess about someone else's layout.
/// </para>
/// </summary>
public static class UiaScroll
{
    /// <summary>
    /// Brings a control into view through <c>ScrollItemPattern</c>, falling back to <c>ScrollPattern</c> for a
    /// container that scrolls as a whole.
    /// <para>
    /// Null when the control offers neither — then the only thing left is scrolling a container by coordinates,
    /// which is the guess this exists to avoid.
    /// </para>
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

    /// <summary>Expands a collapsed node — a tree item, a disclosure — so its children can be seen at all.</summary>
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
/// Reads the UI Automation tree. This is what makes "click the 播放 button" possible: the model addresses a
/// control by what it is, instead of guessing screen coordinates — the failure mode that once typed a letter
/// into whatever window happened to be focused.
/// <para>
/// UIA is per-process RPC, so the walk is bounded on purpose: a depth limit, a cap per window, a cap on the
/// nodes <i>visited</i>, and a per-window time budget — see <see cref="WalkBudget"/>. An app that is blind or
/// slow (Chromium, games, canvases) yields nothing and the window-level path still works.
/// </para>
/// </summary>
public static class UiaTree
{
    /// <summary>How much of a document's text to report: enough to verify a change, not the whole file.</summary>
    private const int MaxValueChars = 200;

    /// <summary>Roles that are actionable; walking into the rest costs time and returns nothing.</summary>
    /// <para>
    /// <c>Text</c> is deliberately not in it. A static label is not something to act on, and it is the most
    /// numerous element in a real interface — so every label consumed part of the 40-control budget that was
    /// meant for controls, and a window with 40 labels above its buttons returned labels and nothing else. The
    /// cost was not just tokens: it was the buttons themselves disappearing from the answer.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Interesting = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "ListItem", "MenuItem", "TabItem",
        "Hyperlink", "Slider", "Spinner", "TreeItem", "DataItem", "SplitButton", "Thumb",
        // A document is the text surface of an editor; its value is how an agent verifies its own typing.
        "Document",
    };

    /// <summary>
    /// Roles whose text is worth reading.
    /// <para>
    /// The value is one cross-process RPC per control — and <c>TextPattern.DocumentRange.GetText</c> pulls a
    /// document range to do it — while the answer is only meaningful for something that holds text the user
    /// typed. Static <c>Text</c> controls are the most numerous elements in a real interface, so reading all
    /// of them was the single largest cost in an observation, and none of it was ever used.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ValueBearing = new(StringComparer.Ordinal)
    {
        "Edit", "Document", "ComboBox",
    };

    /// <summary>
    /// Whether this role's text is read. Exposed for the same reason <see cref="UiaTree.TryBuildPointer"/>-style
    /// internals are: the rule is a contract, and asserting it needs no window.
    /// </summary>
    internal static bool ReadsValue(string role) => ValueBearing.Contains(role);

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
    /// <para>
    /// The default budget is deliberately the one <see cref="TryAct"/> walks with too: an id is an index into
    /// this list, so a different budget here than there would mean the index names a different control.
    /// </para>
    /// </summary>
    public static IReadOnlyList<UiElement> ReadNumbered(nint handle, string windowId) =>
        Number(WalkWindow(handle, windowId, WalkBudget.Default).Elements, windowId);

    /// <summary>
    /// The same walk, keeping the live controls behind the numbered elements.
    /// <para>
    /// An action needs both halves: the numbering says which control the model meant, and the
    /// <see cref="AutomationElement"/> is what a pattern is invoked on. Returning them together is what lets
    /// the caller act without walking the window a second time — and the second walk was the expensive half
    /// of pressing a control by id, because UI Automation is a cross-process RPC per node.
    /// </para>
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
    /// One walk of a window's control view, kept in two lockstep lists: what the model was shown, and the
    /// live <see cref="AutomationElement"/> behind each entry.
    /// <para>
    /// The pairing is what makes an element id actionable later. An id is an index into <i>this</i> list, so
    /// re-walking a window recovers the control that index pointed at — no name search that could match a
    /// different element that happens to share a label.
    /// </para>
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
            // is the deliberate version of this — it stops the walk at a point we choose rather than wherever
            // the provider's own internal timeout happens to land.
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
            // Checked per node rather than per produced element: the old condition counted only elements the
            // walk had decided to keep, so a window made entirely of skipped controls never reached its cap
            // and was walked to its last node — which is exactly the shape a Chromium window has.
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
            IdentityDigest: DesktopObservation.ElementIdentity(windowId, name, role),
            Value: ReadValue(node, role),
            IsOffscreen: current.IsOffscreen);
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
    /// Pattern-shaped: <see cref="ActionKind.LeftClick"/> and <see cref="ActionKind.Focus"/> press a control,
    /// <see cref="ActionKind.ScrollIntoView"/> makes it reachable.
    /// <c>TypeText</c> is deliberately excluded: <c>ValuePattern.SetValue</c> replaces a field's entire
    /// contents, and silently wiping a half-filled form is not what "type this" means. A double click is
    /// excluded too: there is no pattern for "press twice in time", and sending <c>Invoke</c> twice is not it.
    /// </param>
    /// <param name="known">
    /// The walk this action was decided on, when the caller already has it. Re-walking is what turns an id
    /// back into a live control and doubles as an "is it still there" check — but the caller re-observed this
    /// window to re-check the action anyway (<c>Observe(elementsFor:)</c>), and that walk is the fresher of
    /// the two. Passing it here saves a second traversal of the same tree; omitting it walks as before.
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
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<AutomationElement> Nodes)? known = null)
    {
        if (kind is not (ActionKind.LeftClick or ActionKind.Focus or ActionKind.ScrollIntoView))
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

        if (kind == ActionKind.ScrollIntoView)
        {
            // The control knows how far it is from the viewport; a scrolled-by estimate is how "scroll down a
            // bit and look again" became a two-step dance the model had to get right by trial.
            return UiaScroll.ScrollIntoView(node) ?? UiaScroll.Expand(node);
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
