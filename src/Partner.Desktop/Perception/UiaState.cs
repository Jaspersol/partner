using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Partner.Desktop.Perception;

/// <summary>
/// What a control says about itself: checked or not, selected or not, expanded or not, what its value is.
/// <para>
/// A pixel diff answers "did something on the screen change" and is wrong in both directions for a small control:
/// a checkbox that flipped is a few dozen pixels in a large window, under any sensible threshold, and a window that
/// repainted elsewhere makes a click that did nothing look like success. Asking the control is exact: the state
/// before and the state after are either the same string or they are not.
/// </para>
/// </summary>
public static class UiaState
{
    /// <summary>The longest value kept whole; a text box holding a document is compared by its length and ends.</summary>
    private const int ValueChars = 200;

    /// <summary>
    /// The control's readable state as one comparable string, or null when it exposes none of the state patterns
    /// (or has gone away). Two reads are equal exactly when nothing the control reports has changed.
    /// </summary>
    public static string? Read(AutomationElement node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var parts = new List<string>(4);

        try
        {
            if (node.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) && toggle is TogglePattern t)
            {
                parts.Add("开关=" + t.Current.ToggleState);
            }

            if (node.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select) && select is SelectionItemPattern s)
            {
                parts.Add("选中=" + s.Current.IsSelected);
            }

            if (node.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) && expand is ExpandCollapsePattern e)
            {
                parts.Add("展开=" + e.Current.ExpandCollapseState);
            }

            if (node.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern v)
            {
                parts.Add("值=" + Shorten(v.Current.Value));
            }
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static string Shorten(string value) =>
        value.Length <= ValueChars ? value : $"{value[..(ValueChars / 2)]}…（共 {value.Length} 字）…{value[^(ValueChars / 2)..]}";

    /// <summary>
    /// Waits (briefly) for the state to differ from <paramref name="before"/>. Controls apply a pattern call
    /// asynchronously, so one read right after the call can still show the old state.
    /// </summary>
    public static async Task<(bool Changed, string? After)> WaitForChangeAsync(
        Func<string?> read,
        string before,
        int attempts,
        TimeSpan interval,
        CancellationToken ct)
    {
        string? after = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            after = read();

            if (after is null || !string.Equals(after, before, StringComparison.Ordinal))
            {
                return (after is not null, after);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        return (false, after);
    }
}
