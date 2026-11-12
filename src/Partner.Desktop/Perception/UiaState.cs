namespace Partner.Desktop.Perception;

/// <summary>
/// What a control says about itself: checked, selected, expanded, and its value.
/// <para>
/// A pixel diff is wrong in both directions for a small control: a checkbox that flipped is a few dozen pixels
/// in a large window, and a repaint elsewhere makes a click that did nothing look like success. Asking the
/// control is exact: the state before and after are either the same string or they are not.
/// </para>
/// </summary>
public static class UiaState
{
    /// <summary>
    /// The control's readable state as one comparable string, or null when it exposes none of the state patterns
    /// (or has gone away). Two reads are equal exactly when nothing the control reports has changed. A password
    /// field contributes only whether it is empty or filled, never its content.
    /// </summary>
    public static string? Read(IUiNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ReadState();
    }

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
