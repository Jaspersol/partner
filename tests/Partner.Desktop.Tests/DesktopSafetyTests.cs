using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The P0 safety rules: a partial SendInput does not leave the keyboard held, system shortcuts are named,
/// password text never enters the answer, and a control that cannot be told from its twin is refused.
/// </summary>
public sealed class DesktopSafetyTests
{
    private static readonly VirtualDesktop Screen = new(0, 0, 2560, 1440);

    [Fact]
    public void A_partial_key_batch_releases_every_key_it_pressed()
    {
        // Ctrl down, C down, C up, Ctrl up — and the call is cut off after the first two. The user's next
        // keystroke would otherwise carry a held Ctrl and a held C.
        var sender = new PartialSender(delivered: 2);
        var injector = new InputInjector(() => Screen, sender.Send, sender.IsDown);

        var result = injector.KeyPress(0x43, new[] { InputInjector.VirtualControl });

        Assert.False(result.Posted);
        Assert.True(result.ReleasePosted);
        Assert.True(result.ReleaseConfirmed);
        Assert.Equal(2, result.Delivered);
        Assert.Equal(4, result.Expected);

        var release = sender.Batches[^1];
        Assert.Contains(release, input => IsKeyUp(input, InputInjector.VirtualControl));
        Assert.Contains(release, input => IsKeyUp(input, 0x43));
    }

    [Fact]
    public void A_complete_batch_does_not_post_a_release()
    {
        var sender = new PartialSender(delivered: 4);
        var injector = new InputInjector(() => Screen, sender.Send, sender.IsDown);

        var result = injector.KeyPress(0x43, new[] { InputInjector.VirtualControl });

        Assert.True(result.Posted);
        Assert.False(result.ReleasePosted);
        Assert.Single(sender.Batches);
    }

    [Fact]
    public void A_partial_unicode_batch_releases_the_character()
    {
        // Typing is two events per character; a prefix can leave the character key down.
        var sender = new PartialSender(delivered: 1);
        var injector = new InputInjector(() => Screen, sender.Send, sender.IsDown);

        var result = injector.TypeText("a");

        Assert.False(result.Posted);
        Assert.True(result.ReleasePosted);
        var release = sender.Batches[^1];
        var up = Assert.Single(release);
        Assert.Equal(NativeMethods.InputKeyboard, up.Type);
        Assert.NotEqual(0u, up.Union.Keyboard.Flags & NativeMethods.KeyeventfKeyup);
        Assert.NotEqual(0u, up.Union.Keyboard.Flags & NativeMethods.KeyeventfUnicode);
        Assert.Equal('a', up.Union.Keyboard.ScanCode);
    }

    [Fact]
    public void Win_combinations_are_blocked_whatever_the_key()
    {
        Assert.True(SystemShortcutGuard.IsBlocked(0x52, new[] { InputInjector.VirtualWin })); // Win+R
        Assert.True(SystemShortcutGuard.IsBlocked(0x58, new[] { InputInjector.VirtualWin })); // Win+X
        Assert.True(SystemShortcutGuard.IsBlocked(InputInjector.VirtualWin, Array.Empty<ushort>()));
    }

    [Fact]
    public void Alt_f4_and_alt_tab_are_blocked_with_extra_modifiers()
    {
        Assert.True(SystemShortcutGuard.IsBlocked(0x73, new[] { InputInjector.VirtualMenu }));
        Assert.True(SystemShortcutGuard.IsBlocked(0x73, new[] { InputInjector.VirtualMenu, InputInjector.VirtualShift }));
        Assert.True(SystemShortcutGuard.IsBlocked(0x09, new[] { InputInjector.VirtualMenu }));
        Assert.True(SystemShortcutGuard.IsBlocked(0x09, new[] { InputInjector.VirtualMenu, InputInjector.VirtualShift }));
    }

    [Fact]
    public void Ctrl_alt_anything_and_task_manager_are_blocked()
    {
        Assert.True(SystemShortcutGuard.IsBlocked(0x2E, new[] { InputInjector.VirtualControl, InputInjector.VirtualMenu }));
        Assert.True(SystemShortcutGuard.IsBlocked(0x1B, new[] { InputInjector.VirtualControl, InputInjector.VirtualShift }));
    }

    [Fact]
    public void Ordinary_chords_are_not_blocked()
    {
        // The rule must not swallow Ctrl+C, Ctrl+V or Ctrl+Shift+V: those are how a person works in the window.
        Assert.False(SystemShortcutGuard.IsBlocked(0x43, new[] { InputInjector.VirtualControl }));
        Assert.False(SystemShortcutGuard.IsBlocked(0x56, new[] { InputInjector.VirtualControl }));
        Assert.False(SystemShortcutGuard.IsBlocked(0x56, new[] { InputInjector.VirtualControl, InputInjector.VirtualShift }));
    }

    [Fact]
    public void The_governance_hook_reads_only_keypress_calls()
    {
        Assert.Contains(
            "win+r",
            SystemShortcutGuard.DescribeBlocked("""{"kind":"KeyPress","virtualKey":82,"modifiers":["win"]}""")!,
            StringComparison.Ordinal);

        Assert.Null(SystemShortcutGuard.DescribeBlocked("""{"kind":"LeftClick","x":1,"y":1}"""));
        Assert.Null(SystemShortcutGuard.DescribeBlocked("""{"kind":"KeyPress","virtualKey":67,"modifiers":["ctrl"]}"""));
        Assert.Null(SystemShortcutGuard.DescribeBlocked("not json"));

        // The tool's deserializer is case-insensitive; the guard must be too, or this call bypasses approval
        // and then executes as Win+R.
        Assert.Contains(
            "win+r",
            SystemShortcutGuard.DescribeBlocked("""{"Kind":"KeyPress","VirtualKey":82,"Modifiers":["win"]}""")!,
            StringComparison.Ordinal);
        Assert.Contains(
            "win+r",
            SystemShortcutGuard.DescribeBlocked("""{"kind":"keypress","virtualKey":82,"modifiers":["Win"]}""")!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_element_never_carries_its_value()
    {
        var element = CdpDomSource.ToElement(
            new CdpElementRecord("0", "密码", "textbox", 0, 0, 100, 24, "hunter2", Offscreen: false, Secret: true),
            "w",
            100,
            100,
            1);

        Assert.True(element.Secret);
        Assert.Null(element.Value);
    }

    [Fact]
    public void An_unreadable_password_flag_is_treated_as_a_password()
    {
        // Only an explicit false from the provider means "this value is safe to read". A missing property or a
        // failed read must fail closed, or a provider that refuses IsPassword leaks its value instead.
        Assert.True(ComUiNode.SecretWhenUnknown(null));
        Assert.True(ComUiNode.SecretWhenUnknown(true));
        Assert.False(ComUiNode.SecretWhenUnknown(false));
    }

    [Fact]
    public void Twin_controls_with_the_same_identity_are_both_marked_ambiguous()
    {
        var elements = new[]
        {
            Element("w:0", "删除", "same"),
            Element("w:1", "删除", "same"),
            Element("w:2", "保存", "different"),
        };

        var marked = DesktopAutomation.MarkAmbiguous(elements);

        Assert.True(marked[0].Ambiguous);
        Assert.True(marked[1].Ambiguous);
        Assert.False(marked[2].Ambiguous);
    }

    [Fact]
    public void A_guard_refusal_is_never_reported_as_delivered()
    {
        var action = Action(guardPassed: false, guardReason: "app_unresponsive");

        var contract = DesktopResultContract.Classify(action);

        Assert.Equal(DesktopResultContract.AppUnresponsive, contract.Code);
        Assert.Equal(DesktopResultContract.NotDelivered, contract.Delivery);
        Assert.Equal(DesktopResultContract.RetrySafe, contract.Retry);
    }

    [Fact]
    public void A_posted_click_with_no_visible_change_is_unverified_and_unsafe_to_repeat()
    {
        var action = Action(
            guardPassed: true,
            injection: new InjectionResult(true, 0, "click"),
            verify: new VerifyResult(VerifyDecision.NoChange, 0, "test", "no dirty region", 1));

        var contract = DesktopResultContract.Classify(action);

        Assert.Equal(DesktopResultContract.Ok, contract.Code);
        Assert.Equal(DesktopResultContract.PostedUnverified, contract.Delivery);
        Assert.Equal(DesktopResultContract.RetryUnsafe, contract.Retry);
    }

    [Fact]
    public void A_verified_click_is_delivered_but_still_not_safe_to_repeat()
    {
        var action = Action(
            guardPassed: true,
            injection: new InjectionResult(true, 0, "click"),
            verify: new VerifyResult(VerifyDecision.AnyChange, 1, "test", "changed", 1));

        var contract = DesktopResultContract.Classify(action);

        Assert.Equal(DesktopResultContract.DeliveredVerified, contract.Delivery);
        Assert.Equal(DesktopResultContract.RetryUnsafe, contract.Retry);
    }

    [Fact]
    public void A_partial_send_is_delivery_uncertain()
    {
        var action = Action(
            guardPassed: true,
            injection: new InjectionResult(false, 5, "partial", ReleasePosted: true, ReleaseConfirmed: true, Delivered: 2, Expected: 4));

        var contract = DesktopResultContract.Classify(action);

        Assert.Equal(DesktopResultContract.DeliveryUncertain, contract.Delivery);
        Assert.Equal(DesktopResultContract.RetryUnsafe, contract.Retry);
    }

    [Fact]
    public async Task Cancelled_uia_work_does_not_reach_the_provider()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var executed = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            UiaSession.Run(() =>
            {
                executed = true;
                return 1;
            }, cts.Token)));

        Assert.False(executed);
    }

    [Fact]
    public void Uia_work_runs_on_its_own_mta_thread()
    {
        var caller = Environment.CurrentManagedThreadId;

        var worker = UiaSession.Run(() => Environment.CurrentManagedThreadId);

        Assert.NotEqual(caller, worker);
    }

    [Fact]
    public void The_transaction_timeout_is_no_longer_four_times_the_walk_budget()
    {
        Assert.Equal(5000u, UiaSession.TransactionTimeoutMs);
    }

    [Fact]
    public void A_refusal_header_is_the_first_line()
    {
        var result = DesktopResultContract.Refuse(DesktopResultContract.PermDenied, "窗口不在前台");

        Assert.False(result.Ok);
        Assert.StartsWith("{\"code\":\"PERM_DENIED\",\"delivery\":\"not_delivered\",\"retry\":\"safe\"}", result.Content);
    }

    private static UiElement Element(string id, string name, string digest) => new(
        id,
        "w",
        name,
        "Button",
        new PixelRect(0, 0, 40, 20),
        digest);

    private static DesktopAction Action(
        bool guardPassed,
        string? guardReason = null,
        InjectionResult? injection = null,
        VerifyResult? verify = null) => new()
        {
            ActionId = "act-1",
            ObsId = "obs-1",
            EntityId = "w",
            Kind = ActionKind.LeftClick,
            ParamsJson = "()",
            LeaseExpiresAt = DateTimeOffset.UnixEpoch,
            GuardPassed = guardPassed,
            GuardReason = guardReason,
            Injection = injection,
            PostVerify = verify,
        };

    private static bool IsKeyUp(NativeMethods.Input input, ushort key) =>
        input.Type == NativeMethods.InputKeyboard
        && input.Union.Keyboard.VirtualKey == key
        && (input.Union.Keyboard.Flags & NativeMethods.KeyeventfKeyup) != 0;

    /// <summary>
    /// A send that inserts only the first <paramref name="delivered"/> records, and tracks what that left
    /// held so the release batch can be judged against the state a real keyboard would report.
    /// </summary>
    private sealed class PartialSender(int delivered)
    {
        private readonly HashSet<int> _held = new();

        public List<NativeMethods.Input[]> Batches { get; } = new();

        public uint Send(NativeMethods.Input[] inputs)
        {
            Batches.Add(inputs);

            if (Batches.Count == 1)
            {
                foreach (var input in inputs.Take(delivered))
                {
                    if (input.Type == NativeMethods.InputKeyboard
                        && (input.Union.Keyboard.Flags & NativeMethods.KeyeventfKeyup) == 0
                        && input.Union.Keyboard.VirtualKey != 0)
                    {
                        _held.Add(input.Union.Keyboard.VirtualKey);
                    }
                }

                return (uint)delivered;
            }

            foreach (var input in inputs)
            {
                if (input.Type == NativeMethods.InputKeyboard
                    && (input.Union.Keyboard.Flags & NativeMethods.KeyeventfKeyup) != 0
                    && input.Union.Keyboard.VirtualKey != 0)
                {
                    _held.Remove(input.Union.Keyboard.VirtualKey);
                }
            }

            return (uint)inputs.Length;
        }

        public bool IsDown(int virtualKey) => _held.Contains(virtualKey);
    }
}
