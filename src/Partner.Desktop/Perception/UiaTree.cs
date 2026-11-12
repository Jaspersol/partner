using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Partner.Desktop.Input;
using Partner.Desktop.Windows;

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
/// <param name="Value">What a text field holds, when UIA exposes it. Null for a password field.</param>
/// <param name="IsOffscreen">
/// Whether the control is outside the window's visible area (below the fold of a list, collapsed into a tree).
/// Kept in the answer rather than filtered out, which is what makes <c>scrollIntoView</c> possible: an element
/// the walk had thrown away could never be scrolled to. Its click point is outside the window, so an action on
/// one is refused until it has been scrolled to.
/// </param>
/// <param name="Secret">Whether this is a password field. Its value is never read into the answer, telemetry or
/// a verification message; a write into it is reported by empty/filled state only.</param>
/// <param name="Ambiguous">Whether another control in the same window has the same identity. Such an element may
/// not be acted on by id: the id could name either twin.</param>
public sealed record UiElement(
    string ElementId,
    string WindowId,
    string Name,
    string Role,
    PixelRect Rect,
    string IdentityDigest,
    string? Value = null,
    bool IsOffscreen = false,
    bool Secret = false,
    bool Ambiguous = false)
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
/// Reads the UI Automation tree through the COM client, which is what makes "click the play button" possible:
/// the model addresses a control by what it is instead of guessing screen coordinates.
/// <para>
/// The walk is bounded on purpose: depth, controls per window, nodes visited and a per-window time budget; see
/// <see cref="WalkBudget"/>. The client itself runs on a dedicated MTA thread with connection and transaction
/// timeouts (see <see cref="UiaSession"/>), so a busy provider fails one call instead of stalling the whole
/// observation.
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
    /// Roles whose text is worth reading. The value is one cross-process RPC per control, and a document range
    /// answers only after pulling its content, while static <c>Text</c> controls are the most numerous elements.
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
    /// A Chromium renderer exposes an anonymous wrapper for each frame and viewport. Counting those as levels
    /// would spend the depth budget on browser chrome before reaching the page. The wrapper is identified by
    /// having no name, no automation id and no action beyond scrolling.
    /// </summary>
    private static bool IsChromiumWrapper(UiNodeInfo info) =>
        info.Role is "Group" or "Custom" or "Pane"
        && info.Name.Length == 0
        && info.AutomationId.Length == 0
        && !info.InvokeAvailable
        && !info.SelectionItemAvailable
        && !info.ToggleAvailable
        && !info.ValueAvailable
        && !info.ExpandCollapseAvailable
        && !info.ScrollAvailable;

    /// <summary>Whether the window is a Chromium-family surface, whose first read is often incomplete.</summary>
    private static bool IsChromium(IUiNode root) =>
        root.Info.ClassName.Contains("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase)
        || root.Info.ClassName.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)
           && root.Info.ProviderDescription.Contains("Chromium", StringComparison.OrdinalIgnoreCase);

    /// <summary>Raw depth cap for Chromium wrappers, which do not consume logical depth.</summary>
    private const int MaxRawDepth = 50;

    /// <summary>How long a Chromium window's tree is given to settle before its first answer is accepted.</summary>
    private static readonly TimeSpan ChromiumSettleBudget = TimeSpan.FromSeconds(3);

    /// <summary>Between two reads while waiting for a Chromium tree to settle.</summary>
    private static readonly TimeSpan ChromiumSettleStep = TimeSpan.FromMilliseconds(250);

    /// <summary>A window whose first read has fewer controls than this is re-read until it settles.</summary>
    /// <summary>
    /// Interactive elements of one window, by native handle. Returns an empty list when UI Automation has nothing
    /// to say about it; never throws, since a window that cannot be inspected is a window the coordinate path is
    /// for.
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
    /// numbering says which control the model meant, and the <see cref="IUiNode"/> is what a pattern is invoked
    /// on. Returning them together avoids a second walk.
    /// </summary>
    internal static (IReadOnlyList<UiElement> Elements, IReadOnlyList<IUiNode> Nodes, WalkStopReason Stopped)
        ReadNumberedWithNodes(nint handle, string windowId)
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
    /// One walk of a window's control view, in two lockstep lists: what the model was shown, and the live node
    /// behind each entry. An id is an index into this list, so re-walking recovers the control that index
    /// pointed at, with no name search that could match another element.
    /// </summary>
    private static (IReadOnlyList<UiElement> Elements, IReadOnlyList<IUiNode> Nodes, WalkStopReason Stopped)
        WalkWindow(nint handle, string windowId, WalkBudget? budget = null, CancellationToken ct = default)
    {
        var probe = WindowProbe.Check(handle);
        if (!probe.Ok)
        {
            return (Array.Empty<UiElement>(), Array.Empty<IUiNode>(),
                probe.Status == WindowProbeStatus.Gone ? WalkStopReason.Gone : WalkStopReason.Unresponsive);
        }

        try
        {
            return WalkWithSettling(handle, windowId, budget ?? WalkBudget.Default, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return (Array.Empty<UiElement>(), Array.Empty<IUiNode>(), UiaErrors.Classify(error));
        }
    }

    private static (IReadOnlyList<UiElement> Elements, IReadOnlyList<IUiNode> Nodes, WalkStopReason Stopped)
        WalkWithSettling(nint handle, string windowId, WalkBudget budget, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        IReadOnlyList<UiElement>? previousElements = null;

        while (true)
        {
            // Each read is one queued UIA transaction; the wait between reads is on this thread, so the
            // single MTA thread is not sleeping through another window's observation.
            var attempt = UiaSession.Run(uia => WalkOnce(uia, handle, windowId, budget), ct);

            if (!attempt.Chromium
                || attempt.Stopped is WalkStopReason.Fault or WalkStopReason.Refused
                    or WalkStopReason.Unresponsive or WalkStopReason.Gone)
            {
                return (attempt.Elements, attempt.Nodes, attempt.Stopped);
            }

            // The first read of a Chromium tree often reports only the browser shell. Two equal counts in a row
            // mean it stopped growing; the budget stops a page that keeps animating. The count threshold is
            // deliberately not used: a shell alone can already have more than eight controls.
            if (previousElements is not null && previousElements.Count == attempt.Elements.Count)
            {
                return (attempt.Elements, attempt.Nodes, attempt.Stopped);
            }

            previousElements = attempt.Elements;

            if (deadline.Elapsed >= ChromiumSettleBudget)
            {
                return (attempt.Elements, attempt.Nodes, attempt.Stopped);
            }

            // Wait on this thread, not the UIA queue; cancellation ends the wait immediately.
            if (ct.WaitHandle.WaitOne(ChromiumSettleStep))
            {
                throw new OperationCanceledException(ct);
            }
        }
    }

    private sealed record WalkAttempt(
        IReadOnlyList<UiElement> Elements,
        IReadOnlyList<IUiNode> Nodes,
        WalkStopReason Stopped,
        bool Chromium);

    private static WalkAttempt WalkOnce(IUIAutomation uia, nint handle, string windowId, WalkBudget budget)
    {
        var elements = new List<UiElement>();
        var nodes = new List<IUiNode>();
        var state = new WalkState(budget, Stopwatch.StartNew());

        IUiNode? root;
        try
        {
            root = UiaClient.Root(uia, handle);
        }
        catch (Exception error)
        {
            return new WalkAttempt(elements, nodes, UiaErrors.Classify(error), Chromium: false);
        }

        if (root is null)
        {
            return new WalkAttempt(elements, nodes, WalkStopReason.Gone, Chromium: false);
        }

        var chromium = IsChromium(root);

        try
        {
            Walk(root, windowId, depth: 0, rawDepth: 0, chromium, elements, nodes, state);
        }
        catch (Exception error)
        {
            state.Fail(UiaErrors.Classify(error));
        }

        return new WalkAttempt(elements, nodes, state.Stopped, chromium);
    }

    private static void Walk(
        IUiNode node,
        string windowId,
        int depth,
        int rawDepth,
        bool chromium,
        List<UiElement> elements,
        List<IUiNode> nodes,
        WalkState state)
    {
        if (!state.Allows(depth, elements.Count))
        {
            return;
        }

        if (rawDepth > MaxRawDepth)
        {
            state.Fail(WalkStopReason.Depth);
            return;
        }

        IUiNode? child;
        try
        {
            child = node.FirstChild();
        }
        catch (Exception error)
        {
            state.Fail(UiaErrors.Classify(error));
            return;
        }

        while (child is not null)
        {
            // Checked per node rather than per produced element: a produced-only cap is never reached by a
            // window made entirely of skipped controls, so such a window would be walked to its last node.
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

            var wrapper = chromium && IsChromiumWrapper(child.Info);
            Walk(child, windowId, wrapper ? depth : depth + 1, rawDepth + 1, chromium, elements, nodes, state);

            try
            {
                child = child.NextSibling();
            }
            catch (Exception error)
            {
                state.Fail(UiaErrors.Classify(error));
                return;
            }
        }
    }

    /// <summary>
    /// The element as the model sees it, or null when it is not something to act on. A password field keeps its
    /// role and name but never its value.
    /// </summary>
    private static UiElement? Describe(IUiNode node, string windowId)
    {
        UiNodeInfo info;
        try
        {
            info = node.Info;
        }
        catch (Exception error) when (UiaErrors.IsPatternMissing(error))
        {
            return null;
        }

        var role = info.Role;
        var name = info.Name;

        // An unnamed element is only worth reporting when it is a thing you can press; a nameless pane is
        // layout, and layout is noise.
        var actionable = Interesting.Contains(role);
        if (!actionable || (name.Length == 0 && role is not ("Button" or "ListItem" or "MenuItem")))
        {
            return null;
        }

        // A disabled control is dropped: there is no action that means "press the greyed-out button". An
        // offscreen one is kept — that is what makes scrolling to it possible.
        if (!info.IsEnabled)
        {
            return null;
        }

        var rect = info.Rect;
        if (rect.IsEmpty || rect.Width < 2 || rect.Height < 2)
        {
            return null;
        }

        string? value = null;
        if (!info.IsPassword && ReadsValue(role))
        {
            try
            {
                value = node.ReadValue();
            }
            catch (Exception error) when (UiaErrors.IsPatternMissing(error))
            {
                value = null;
            }
        }

        return new UiElement(
            ElementId: string.Empty, // assigned by the observation, which knows the window's position in it
            WindowId: windowId,
            Name: name,
            Role: role,
            Rect: rect,
            IdentityDigest: DesktopObservation.ElementIdentity(windowId, name, role, info.StableKey),
            Value: value,
            IsOffscreen: info.IsOffscreen,
            Secret: info.IsPassword);
    }

    /// <summary>The live control behind an element id, when the walk that numbered it is still the one in hand.</summary>
    public static IUiNode? NodeOf(
        string windowId,
        string elementId,
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<IUiNode> Nodes) known)
    {
        var index = ParseIndex(windowId, elementId);
        return index >= 0 && index < known.Nodes.Count ? known.Nodes[index] : null;
    }

    /// <summary>
    /// Whether the live node still has the identity the observation gave the element at that index. Exposed
    /// so the rule can be asserted without a window.
    /// </summary>
    internal static bool MatchesIdentity(string windowId, IUiNode node, string expectedIdentity) =>
        string.Equals(IdentityOf(windowId, node), expectedIdentity, StringComparison.Ordinal);

    /// <summary>The identity digest of a live node, computed the same way the observation computed it.</summary>
    internal static string IdentityOf(string windowId, IUiNode node)
    {
        var info = node.Info;
        return DesktopObservation.ElementIdentity(windowId, info.Name, info.Role, info.StableKey);
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
    /// Gives the control the keyboard focus, and confirms it actually has it. Returns null on success or the
    /// refusal to carry: the window being foreground is not the same as the right control having the caret.
    /// </summary>
    public static string? EnsureKeyboardFocus(
        nint handle,
        string windowId,
        string elementId,
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<IUiNode> Nodes)? known,
        string? expectedIdentity = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<IUiNode> nodes;
        if (known is { } pair)
        {
            nodes = pair.Nodes;
        }
        else
        {
            nodes = WalkWindow(handle, windowId, ct: ct).Nodes;
        }

        if (Index(windowId, elementId, nodes.Count) is not { } index)
        {
            return $"未知控件 '{elementId}'：重新 desktop_observe";
        }

        // A re-walk can shift the index: an inserted control or a newly expanded list moves every later
        // control by one. Acting on whatever now sits at that index is the wrong-control click this check
        // exists to prevent.
        if (expectedIdentity is not null && !MatchesIdentity(windowId, nodes[index], expectedIdentity))
        {
            return $"控件 '{elementId}' 在重新遍历后已经不在这个位置（STALE_REF）：重新 desktop_observe 再决定";
        }

        var node = nodes[index];
        var focused = node.SetFocus();
        if (focused is null)
        {
            return $"'{elementId}' 不支持 SetFocus，无法确认键盘焦点在它身上：先 desktop_observe 换一个控件，或改用坐标点击";
        }

        if (!focused.Ok)
        {
            return focused.Detail + "：键盘输入会打到当前焦点所在的控件，已拒绝";
        }

        // Pattern calls are asynchronous, and a focus change may wait for the window's input queue.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (node.HasKeyboardFocus())
            {
                return null;
            }

            Thread.Sleep(40);
        }

        var current = UiaSession.FocusedNode();
        var where = current is null
            ? "当前焦点读不到"
            : $"当前焦点在 '{current.Info.Name}' [{current.Info.Role}]";

        return $"控件 '{elementId}' 没有拿到键盘焦点（{where}）：输入会打到错的地方，已拒绝";
    }

    private static int? Index(string windowId, string elementId, int count)
    {
        var index = ParseIndex(windowId, elementId);
        return index >= 0 && index < count ? index : null;
    }

    /// <summary>
    /// Acts on a named control through a UI Automation pattern instead of a coordinate click, when the control
    /// supports one. Returns null when this does not apply; the caller then injects input at the element centre.
    /// </summary>
    public static UiaInvocation? TryAct(
        nint handle,
        string windowId,
        string elementId,
        ActionKind kind,
        (IReadOnlyList<UiElement> Elements, IReadOnlyList<IUiNode> Nodes)? known = null,
        ActionParams? parameters = null,
        bool background = false,
        string? expectedIdentity = null,
        CancellationToken ct = default)
    {
        // Wheel and typing are patterns only when the caller has agreed to the trade: ScrollPattern moves a
        // container rather than turning the wheel over it, and ValuePattern appends rather than types.
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

        // Re-walking turns the id back into a live control and doubles as an "is it still there" check.
        IReadOnlyList<IUiNode> nodes;

        if (known is { } pair)
        {
            nodes = pair.Nodes;
        }
        else
        {
            nodes = WalkWindow(handle, windowId, ct: ct).Nodes;
        }

        if (index >= nodes.Count)
        {
            return null;
        }

        // The index is only a handle to the walk it came from. A re-walk can insert or remove a control above
        // it, and then this index names a different control; the identity digest says which one it is now.
        if (expectedIdentity is not null && !MatchesIdentity(windowId, nodes[index], expectedIdentity))
        {
            return new UiaInvocation(
                false,
                "uia.stale",
                "重新遍历后该序号指向了另一个控件（STALE_REF）：重新 desktop_observe 再决定");
        }

        var node = nodes[index];

        return kind switch
        {
            ActionKind.Focus => node.SetFocus(),
            ActionKind.Scroll => node.ScrollBy(parameters?.ScrollDelta ?? 0),
            ActionKind.TypeText => node.AppendText(parameters?.Text ?? string.Empty),

            // The control knows how far it is from the viewport; scrolling by estimate is the guess this avoids.
            ActionKind.ScrollIntoView => node.ScrollItemIntoView() ?? node.Expand(),

            // Which pattern means "click" depends on what the control is: pressing a list row selects it rather
            // than invoking it, and pressing a checkbox flips it. The wrong pattern is a different action.
            _ => node.Info.Role switch
            {
                "CheckBox" => node.Toggle() ?? node.Invoke(),
                "RadioButton" or "ListItem" or "TabItem" or "DataItem" or "TreeItem" =>
                    node.Select() ?? node.Invoke(),
                _ => node.Invoke() ?? node.Select(),
            },
        };
    }
}

/// <summary>Creates the root node and decides whether this provider is worth caching.</summary>
internal static class UiaClient
{
    internal static IUiNode? Root(IUIAutomation automation, nint handle)
    {
        var current = automation.ElementFromHandle(handle);
        if (current is null)
        {
            return null;
        }

        // A provider in this process answers without a cross-process round trip, so a cache request only adds
        // memory traffic. A provider in another process is exactly the case caching is for.
        var sameProcess = current.CurrentProcessId == Environment.ProcessId;
        var cache = sameProcess ? null : BuildCacheRequest(automation);
        var element = cache is null
            ? current
            : automation.ElementFromHandleBuildCache(handle, cache) ?? current;

        return new ComUiNode(automation.ControlViewWalker, cache, element);
    }

    private static IUIAutomationCacheRequest BuildCacheRequest(IUIAutomation automation)
    {
        var cache = automation.CreateCacheRequest();
        cache.TreeScope = TreeScope.TreeScope_Element;

        // Full, not None: a cached element in None mode is a property bag that cannot always be used as the
        // starting point for the next tree-walker step. With None the walk silently returned about half the
        // controls on an Edge page (63 vs 129).
        cache.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;

        foreach (var property in new[]
                 {
                     UiaIds.RuntimeId, UiaIds.BoundingRectangle, UiaIds.ProcessId, UiaIds.ControlType,
                     UiaIds.Name, UiaIds.HasKeyboardFocus, UiaIds.IsEnabled, UiaIds.AutomationId,
                     UiaIds.ClassName, UiaIds.IsPassword, UiaIds.IsOffscreen,
                     UiaIds.IsInvokePatternAvailable, UiaIds.IsSelectionItemPatternAvailable,
                     UiaIds.IsTogglePatternAvailable, UiaIds.IsValuePatternAvailable,
                     UiaIds.IsExpandCollapsePatternAvailable, UiaIds.IsScrollPatternAvailable,
                     UiaIds.IsScrollItemPatternAvailable, UiaIds.IsTextPatternAvailable,
                     UiaIds.ProviderDescription,
                 })
        {
            try
            {
                cache.AddProperty(property);
            }
            catch (COMException)
            {
                // A property the provider cannot cache is read on demand; one unsupported property must not
                // turn the whole walk into a fallback.
            }
        }

        return cache;
    }
}
