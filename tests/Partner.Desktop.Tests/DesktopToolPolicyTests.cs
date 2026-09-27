using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// Which windows the model may act on. A title filter is for narrowing an installation; the gate that
/// actually protects the user is the approval on the action.
/// </summary>
public sealed class DesktopToolPolicyTests
{
    [Fact]
    public void An_open_policy_allows_any_window()
    {
        var policy = DesktopToolPolicy.Any;

        Assert.True(policy.Allows("记事本"));
        Assert.True(policy.Allows("anything at all"));
        Assert.True(policy.Allows(null));
    }

    [Fact]
    public void A_title_substring_allows_the_match_and_nothing_else()
    {
        var policy = new DesktopToolPolicy(new[] { "记事本", "Calculator" });

        // Case-insensitive, and a substring rather than the whole title: window titles carry document names.
        Assert.True(policy.Allows("未命名 - 记事本"));
        Assert.True(policy.Allows("calculator plus"));
        Assert.False(policy.Allows("浏览器"));
        Assert.False(policy.Allows(""));
        Assert.False(policy.Allows(null));
    }

    [Fact]
    public void Blank_entries_do_not_open_the_policy()
    {
        // An empty line in the settings box must not become "allow everything".
        var policy = new DesktopToolPolicy(new[] { "  " });

        Assert.False(policy.Allows("记事本"));
    }
}
