using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Perception;

/// <summary>The properties one UI Automation node reports, read once when the node is reached.</summary>
/// <param name="Role">Control type in the vocabulary the observation already uses.</param>
/// <param name="StableKey">AutomationId, class and runtime id — what distinguishes this control from its twin.</param>
public sealed record UiNodeInfo(
    string Role,
    string Name,
    PixelRect Rect,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsPassword,
    string? StableKey,
    string ClassName,
    string AutomationId,
    int ProcessId,
    string ProviderDescription,
    bool InvokeAvailable,
    bool SelectionItemAvailable,
    bool ToggleAvailable,
    bool ValueAvailable,
    bool ExpandCollapseAvailable,
    bool ScrollAvailable,
    bool ScrollItemAvailable,
    bool TextAvailable)
{
    public bool HasKeyboardFocus { get; init; }

    /// <summary>Whether the provider lives in this process. Caching an in-process provider is slower.</summary>
    public bool SameProcess { get; init; }
}

/// <summary>
/// One control in a UI Automation tree, independent of the client used to reach it. The walk and the action
/// layer only need these operations, which keeps the COM details out of both and lets a test supply a node
/// without a desktop.
/// </summary>
public interface IUiNode
{
    UiNodeInfo Info { get; }

    IUiNode? FirstChild();

    IUiNode? NextSibling();

    IUiNode? Parent();

    /// <summary>The control's text, when it has one and it is not a password field.</summary>
    string? ReadValue();

    /// <summary>A comparable description of the state patterns the control exposes, or null when it has none.</summary>
    string? ReadState();

    bool HasKeyboardFocus();

    UiaInvocation? Invoke();

    UiaInvocation? Select();

    UiaInvocation? Toggle();

    UiaInvocation? SetFocus();

    UiaInvocation? ScrollItemIntoView();

    UiaInvocation? ScrollBy(int notches);

    UiaInvocation? AppendText(string text);

    UiaInvocation? Expand();
}

/// <summary>UI Automation property and pattern ids, named so the call sites read as what they are.</summary>
internal static class UiaIds
{
    internal const int RuntimeId = 30000;
    internal const int BoundingRectangle = 30001;
    internal const int ProcessId = 30002;
    internal const int ControlType = 30003;
    internal const int Name = 30005;
    internal const int HasKeyboardFocus = 30008;
    internal const int IsEnabled = 30010;
    internal const int AutomationId = 30011;
    internal const int ClassName = 30012;
    internal const int IsPassword = 30019;
    internal const int IsOffscreen = 30022;
    internal const int IsInvokePatternAvailable = 30031;
    internal const int IsSelectionItemPatternAvailable = 30036;
    internal const int IsTogglePatternAvailable = 30041;
    internal const int IsValuePatternAvailable = 30043;
    internal const int IsExpandCollapsePatternAvailable = 30028;
    internal const int IsScrollPatternAvailable = 30034;
    internal const int IsScrollItemPatternAvailable = 30035;
    internal const int IsTextPatternAvailable = 30040;
    internal const int ProviderDescription = 30107;

    internal const int InvokePattern = 10000;
    internal const int ValuePattern = 10002;
    internal const int ScrollPattern = 10004;
    internal const int ExpandCollapsePattern = 10005;
    internal const int SelectionItemPattern = 10010;
    internal const int TextPattern = 10014;
    internal const int TogglePattern = 10015;
    internal const int ScrollItemPattern = 10017;
}

/// <summary>Maps what UI Automation says a control is onto the vocabulary the observation already uses.</summary>
internal static class UiaRoles
{
    internal static string OfControlType(int controlType) => controlType switch
    {
        50000 => "Button",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50007 => "ListItem",
        50011 => "MenuItem",
        50013 => "RadioButton",
        50015 => "Slider",
        50016 => "Spinner",
        50019 => "TabItem",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        _ => "Unknown",
    };
}

/// <summary>
/// The HRESULTs UI Automation providers report. They are classified because the model needs to tell "there is
/// nothing there" from "we were refused", "the window is hung" and "the walk broke" — the next step differs.
/// </summary>
internal static class UiaErrors
{
    private const int ElementNotAvailable = unchecked((int)0x80040201);
    private const int ElementNotEnabled = unchecked((int)0x80040200);
    private const int NotSupported = unchecked((int)0x80040204);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int RPCTimeout = unchecked((int)0x8001011F);
    private const int CallRejected = unchecked((int)0x80010001);
    private const int ServerCallRetry = unchecked((int)0x8001010A);
    private const int Disconnected = unchecked((int)0x80010108);

    /// <summary>A missing pattern is not an error: the caller falls back to another channel.</summary>
    internal static bool IsPatternMissing(Exception error) =>
        error is InvalidCastException
        || (error is COMException com && com.HResult is ElementNotAvailable or ElementNotEnabled or NotSupported);

    /// <summary>
    /// A property read that failed. One unsupported property must not fail the whole walk: the node is still
    /// worth reporting with the properties it did answer.
    /// </summary>
    internal static bool IsReadFailure(Exception error) =>
        error is InvalidOperationException or InvalidCastException
        || (error is COMException com && com.HResult is NotSupported or ElementNotEnabled);

    internal static WalkStopReason Classify(Exception error) => error switch
    {
        TimeoutException => WalkStopReason.Unresponsive,
        UnauthorizedAccessException => WalkStopReason.Refused,
        COMException com => com.HResult switch
        {
            AccessDenied => WalkStopReason.Refused,
            RPCTimeout or CallRejected or ServerCallRetry => WalkStopReason.Unresponsive,
            ElementNotAvailable or Disconnected => WalkStopReason.Gone,
            _ => WalkStopReason.Fault,
        },
        _ => WalkStopReason.Fault,
    };
}

/// <summary>
/// One COM UI Automation element, plus the walker and cache request that produced it. The tree walk that reads
/// it runs on the automation's own MTA thread (see <see cref="UiaSession"/>); the element itself is a
/// free-threaded UI Automation client object, so a short pattern call from an action thread is safe. The heavy,
/// multi-node work — the walk — is what must not run on the caller's apartment.
/// </summary>
internal sealed class ComUiNode : IUiNode
{
    private readonly IUIAutomationTreeWalker _walker;
    private readonly IUIAutomationCacheRequest? _cache;
    private readonly IUIAutomationElement _element;
    private readonly bool _cached;

    internal ComUiNode(
        IUIAutomationTreeWalker walker,
        IUIAutomationCacheRequest? cache,
        IUIAutomationElement element)
    {
        _walker = walker;
        _cache = cache;
        _element = element;
        _cached = cache is not null;
        Info = UiaSession.Run(ReadInfo);
    }

    public UiNodeInfo Info { get; }

    public IUiNode? FirstChild() => UiaSession.Run(() =>
    {
        var child = _cached
            ? _walker.GetFirstChildElementBuildCache(_element, _cache!)
            : _walker.GetFirstChildElement(_element);

        return child is null ? null : new ComUiNode(_walker, _cache, child);
    });

    public IUiNode? NextSibling() => UiaSession.Run(() =>
    {
        var sibling = _cached
            ? _walker.GetNextSiblingElementBuildCache(_element, _cache!)
            : _walker.GetNextSiblingElement(_element);

        return sibling is null ? null : new ComUiNode(_walker, _cache, sibling);
    });

    public IUiNode? Parent() => UiaSession.Run(() =>
    {
        var parent = _cached
            ? _walker.GetParentElementBuildCache(_element, _cache!)
            : _walker.GetParentElement(_element);

        return parent is null ? null : new ComUiNode(_walker, _cache, parent);
    });

    public bool HasKeyboardFocus() => UiaSession.Run(() =>
    {
        try
        {
            return _element.CurrentHasKeyboardFocus != 0;
        }
        catch (Exception error) when (UiaErrors.IsReadFailure(error))
        {
            return false;
        }
    });

    public string? ReadValue() => UiaSession.Run(() =>
    {
        if (Info.IsPassword)
        {
            // The provider would answer; not reading it is the point. Callers that need to know whether the
            // field changed ask ReadState, which reports only empty/filled.
            return null;
        }

        try
        {
            if (_element.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern value)
            {
                return value.CurrentValue;
            }

            if (_element.GetCurrentPattern(UiaIds.TextPattern) is IUIAutomationTextPattern text)
            {
                var content = text.DocumentRange.GetText(200);
                return content.Length == 0 ? null : content;
            }

            return null;
        }
        catch (Exception error) when (UiaErrors.IsReadFailure(error))
        {
            return null;
        }
    });

    public string? ReadState() => UiaSession.Run(() =>
    {
        var parts = new List<string>(4);

        try
        {
            if (_element.GetCurrentPattern(UiaIds.TogglePattern) is IUIAutomationTogglePattern toggle)
            {
                parts.Add("开关=" + toggle.CurrentToggleState);
            }

            if (_element.GetCurrentPattern(UiaIds.SelectionItemPattern) is IUIAutomationSelectionItemPattern select)
            {
                parts.Add("选中=" + (select.CurrentIsSelected != 0));
            }

            if (_element.GetCurrentPattern(UiaIds.ExpandCollapsePattern) is IUIAutomationExpandCollapsePattern expand)
            {
                parts.Add("展开=" + expand.CurrentExpandCollapseState);
            }

            if (_element.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern value)
            {
                // A password's content is never compared or reported. Empty vs filled is enough to see that a
                // write reached the field, without a value-derived fingerprint that could be attacked offline.
                parts.Add(Info.IsPassword
                    ? "值=" + (string.IsNullOrEmpty(value.CurrentValue) ? "空" : "已填")
                    : "值=" + Shorten(value.CurrentValue));
            }
        }
        catch (Exception error) when (UiaErrors.IsReadFailure(error))
        {
            return null;
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    });

    public UiaInvocation? Invoke() => InvokePattern(
        UiaIds.InvokePattern,
        pattern => ((IUIAutomationInvokePattern)pattern).Invoke(),
        "uia.invoke",
        "已用 InvokePattern 触发（不需要前台窗口）");

    public UiaInvocation? Select() => InvokePattern(
        UiaIds.SelectionItemPattern,
        pattern => ((IUIAutomationSelectionItemPattern)pattern).Select(),
        "uia.select",
        "已用 SelectionItemPattern 选中（不需要前台窗口）");

    public UiaInvocation? Toggle() => InvokePattern(
        UiaIds.TogglePattern,
        pattern => ((IUIAutomationTogglePattern)pattern).Toggle(),
        "uia.toggle",
        "已用 TogglePattern 切换（不需要前台窗口）");

    public UiaInvocation? SetFocus() => UiaSession.Run(() =>
    {
        try
        {
            _element.SetFocus();
            return new UiaInvocation(true, "uia.setfocus", "已用 SetFocus 聚焦该控件");
        }
        catch (Exception error) when (UiaErrors.IsPatternMissing(error))
        {
            return null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.setfocus", $"SetFocus 调用失败：{error.GetType().Name}");
        }
    });

    public UiaInvocation? Expand() => InvokePattern(
        UiaIds.ExpandCollapsePattern,
        pattern => ((IUIAutomationExpandCollapsePattern)pattern).Expand(),
        "uia.expand",
        "已用 ExpandCollapsePattern 展开该控件");

    public UiaInvocation? ScrollItemIntoView() => UiaSession.Run(() =>
    {
        try
        {
            if (_element.GetCurrentPattern(UiaIds.ScrollItemPattern) is IUIAutomationScrollItemPattern item)
            {
                item.ScrollIntoView();
                return new UiaInvocation(true, "uia.scrollintoview", "已用 ScrollItemPattern 把该控件滚入视野");
            }

            if (_element.GetCurrentPattern(UiaIds.ScrollPattern) is IUIAutomationScrollPattern scroll)
            {
                scroll.Scroll(ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeIncrement);
                return new UiaInvocation(true, "uia.scroll", "已用 ScrollPattern 滚动容器");
            }

            return null;
        }
        catch (Exception error) when (UiaErrors.IsPatternMissing(error))
        {
            return null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.scrollintoview", $"滚动入视野失败：{error.GetType().Name}");
        }
    });

    public UiaInvocation? ScrollBy(int notches) => UiaSession.Run(() =>
    {
        if (notches == 0)
        {
            return null;
        }

        try
        {
            IUiNode? current = this;
            for (var depth = 0; depth <= 8 && current is not null; depth++)
            {
                if (current is ComUiNode node
                    && node._element.GetCurrentPattern(UiaIds.ScrollPattern) is IUIAutomationScrollPattern scroll
                    && scroll.CurrentVerticallyScrollable != 0)
                {
                    var amount = notches > 0
                        ? ScrollAmount.ScrollAmount_SmallDecrement
                        : ScrollAmount.ScrollAmount_SmallIncrement;
                    var steps = Math.Min(60, Math.Abs(notches) * 3);

                    for (var step = 0; step < steps; step++)
                    {
                        scroll.Scroll(ScrollAmount.ScrollAmount_NoAmount, amount);
                    }

                    return new UiaInvocation(
                        true,
                        "uia.scrollby",
                        $"已用 ScrollPattern 滚动{(depth == 0 ? "该控件" : "它上层的可滚动容器")}"
                        + "（后台：不需要前台，也不动用户的指针）");
                }

                current = current.Parent();
            }

            return null;
        }
        catch (Exception error) when (UiaErrors.IsPatternMissing(error))
        {
            return null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.scrollby", $"滚动失败：{error.GetType().Name}");
        }
    });

    public UiaInvocation? AppendText(string text) => UiaSession.Run(() =>
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            if (_element.GetCurrentPattern(UiaIds.ValuePattern) is not IUIAutomationValuePattern value
                || value.CurrentIsReadOnly != 0)
            {
                return null;
            }

            var existing = value.CurrentValue ?? string.Empty;
            value.SetValue(existing + text);

            return new UiaInvocation(
                true,
                "uia.value",
                existing.Length == 0
                    ? $"已用 ValuePattern 写入 {text.Length} 个字符（字段原本为空；后台：不需要前台）"
                    : $"已用 ValuePattern 把 {text.Length} 个字符追加到原有 {existing.Length} 个字符之后"
                      + "（后台下无法知道光标在哪，所以只能加在末尾）");
        }
        catch (Exception error) when (UiaErrors.IsPatternMissing(error))
        {
            return null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, "uia.value", $"ValuePattern 写入失败：{error.GetType().Name}");
        }
    });

    private UiNodeInfo ReadInfo()
    {
        var controlType = ReadInt(UiaIds.ControlType);
        var role = UiaRoles.OfControlType(controlType);
        // A failed or unsupported IsPassword read is treated as a password: the cost of guessing wrong in the
        // other direction is plaintext in the model's context.
        var password = SecretWhenUnknown(Read(UiaIds.IsPassword));
        var automationId = ReadString(UiaIds.AutomationId) ?? string.Empty;
        var className = ReadString(UiaIds.ClassName) ?? string.Empty;
        var runtime = ReadRuntimeId();
        var stable = automationId.Length == 0 && className.Length == 0 && runtime.Length == 0
            ? null
            : $"{automationId}|{className}|{runtime}";

        return new UiNodeInfo(
            Role: role,
            Name: ReadString(UiaIds.Name) ?? string.Empty,
            Rect: ReadRect(),
            IsEnabled: ReadBool(UiaIds.IsEnabled),
            IsOffscreen: ReadBool(UiaIds.IsOffscreen),
            IsPassword: password,
            StableKey: stable,
            ClassName: className,
            AutomationId: automationId,
            ProcessId: ReadInt(UiaIds.ProcessId),
            ProviderDescription: ReadString(UiaIds.ProviderDescription) ?? string.Empty,
            InvokeAvailable: ReadBool(UiaIds.IsInvokePatternAvailable),
            SelectionItemAvailable: ReadBool(UiaIds.IsSelectionItemPatternAvailable),
            ToggleAvailable: ReadBool(UiaIds.IsTogglePatternAvailable),
            ValueAvailable: ReadBool(UiaIds.IsValuePatternAvailable),
            ExpandCollapseAvailable: ReadBool(UiaIds.IsExpandCollapsePatternAvailable),
            ScrollAvailable: ReadBool(UiaIds.IsScrollPatternAvailable),
            ScrollItemAvailable: ReadBool(UiaIds.IsScrollItemPatternAvailable),
            TextAvailable: ReadBool(UiaIds.IsTextPatternAvailable))
        {
            HasKeyboardFocus = ReadBool(UiaIds.HasKeyboardFocus),
            SameProcess = ReadInt(UiaIds.ProcessId) == Environment.ProcessId,
        };
    }

    private object? Read(int propertyId)
    {
        try
        {
            return _cached
                ? _element.GetCachedPropertyValue(propertyId)
                : _element.GetCurrentPropertyValue(propertyId);
        }
        catch (Exception error) when (UiaErrors.IsReadFailure(error))
        {
            return null;
        }
    }

    private string? ReadString(int propertyId) => Read(propertyId) as string;

    /// <summary>
    /// Whether the control must be treated as a password field. Only an explicit <c>false</c> from the provider
    /// says it is safe to read the value; a missing property, an unsupported read or a failed read does not.
    /// </summary>
    internal static bool SecretWhenUnknown(object? isPassword) => isPassword is not bool known || known;

    private bool ReadBool(int propertyId) => Read(propertyId) is bool value && value;

    private int ReadInt(int propertyId) => Read(propertyId) is int value ? value : 0;

    private string ReadRuntimeId()
    {
        if (Read(UiaIds.RuntimeId) is int[] cached)
        {
            return string.Join('.', cached);
        }

        try
        {
            return string.Join('.', _element.GetRuntimeId());
        }
        catch (Exception error) when (UiaErrors.IsReadFailure(error))
        {
            return string.Empty;
        }
    }

    private PixelRect ReadRect()
    {
        // The COM property is a four-element array in left, top, width, height order. Treating the last two
        // as right/bottom subtracts the origin twice and produced negative sizes.
        if (Read(UiaIds.BoundingRectangle) is double[] values && values.Length >= 4)
        {
            return new PixelRect(
                (int)Math.Round(values[0]),
                (int)Math.Round(values[1]),
                (int)Math.Round(values[2]),
                (int)Math.Round(values[3]));
        }

        if (Read(UiaIds.BoundingRectangle) is tagRECT rect)
        {
            return new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }

        try
        {
            var bounds = _element.CurrentBoundingRectangle;
            return new PixelRect(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);
        }
        catch (Exception error) when (UiaErrors.IsReadFailure(error))
        {
            return new PixelRect(0, 0, 0, 0);
        }
    }

    private UiaInvocation? InvokePattern(int patternId, Action<object> action, string method, string detail) => UiaSession.Run(() =>
    {
        object? pattern;
        try
        {
            pattern = _element.GetCurrentPattern(patternId);
        }
        catch (Exception error) when (UiaErrors.IsPatternMissing(error))
        {
            return null;
        }

        if (pattern is null)
        {
            return null;
        }

        try
        {
            action(pattern);
            return new UiaInvocation(true, method, detail);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return new UiaInvocation(false, method, $"{method} 调用失败：{error.GetType().Name}");
        }
    });

    private const int ValueChars = 200;

    private static string Shorten(string value) =>
        value.Length <= ValueChars ? value : $"{value[..(ValueChars / 2)]}…（共 {value.Length} 字）…{value[^(ValueChars / 2)..]}";
}

/// <summary>
/// The one place COM UI Automation is created. A dedicated MTA thread owns it: UI Automation client calls are
/// cross-process RPCs, and running them on the caller's apartment (often STA, because the host uses WPF) makes
/// a busy provider block the thread the rest of the application needs. The connection and transaction timeouts
/// are set on <c>IUIAutomation2</c>, so a hung provider fails a call instead of stalling the walk forever.
/// </summary>
internal static class UiaSession
{
    /// <summary>Per-call connection timeout. A provider that does not answer within this is treated as hung.</summary>
    internal const uint ConnectionTimeoutMs = 2000;

    /// <summary>Upper bound on one transaction, including a provider that answers slowly but does answer.</summary>
    internal const uint TransactionTimeoutMs = 5000;

    private static readonly Lazy<Dispatcher> Shared = new(() => new Dispatcher(), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static T Run<T>(Func<IUIAutomation, T> work, CancellationToken ct = default) =>
        Shared.Value.Run(work, ct);

    /// <summary>The same queue for work that already holds the elements it needs.</summary>
    internal static T Run<T>(Func<T> work, CancellationToken ct = default) =>
        Shared.Value.Run(_ => work(), ct);

    /// <summary>The focused control, for the keyboard-focus gate. Null when it cannot be read.</summary>
    internal static ComUiNode? FocusedNode()
    {
        try
        {
            return Run(uia =>
            {
                var focused = uia.GetFocusedElement();
                return focused is null ? null : new ComUiNode(uia.ControlViewWalker, null, focused);
            });
        }
        catch (Exception error) when (error is COMException or TimeoutException)
        {
            return null;
        }
    }

    private sealed class Dispatcher
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private IUIAutomation? _automation;

        internal Dispatcher()
        {
            _thread = new Thread(RunQueue)
            {
                IsBackground = true,
                Name = "partner-uia",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            Ready.Wait();
        }

        private readonly ManualResetEventSlim _ready = new(false);

        private ManualResetEventSlim Ready => _ready;

        private void RunQueue()
        {
            try
            {
                NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
                var automation = (IUIAutomation)new CUIAutomation8();
                if (automation is IUIAutomation2 settings)
                {
                    settings.ConnectionTimeout = ConnectionTimeoutMs;
                    settings.TransactionTimeout = TransactionTimeoutMs;
                }

                _automation = automation;
            }
            finally
            {
                _ready.Set();
            }

            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }

        internal T Run<T>(Func<IUIAutomation, T> work, CancellationToken ct)
        {
            var automation = _automation ?? throw new InvalidOperationException("UI Automation is not available");
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (Thread.CurrentThread == _thread)
            {
                return work(automation);
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                // A cancelled observation is not worth a provider round trip; dropping it here keeps the queue
                // moving for the turns that are still wanted.
                if (ct.IsCancellationRequested)
                {
                    completion.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    completion.TrySetResult(work(automation));
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            });

            return completion.Task.GetAwaiter().GetResult();
        }
    }
}
