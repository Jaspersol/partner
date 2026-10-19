using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The session's memory of which hosts it has seen. Governance asks this one question — did this host come from a
/// search result the user was shown, or from the model's own choice — so being too forgetful turns an ordinary
/// "open that link" into an approval prompt, and being too generous lets the model pick its own destination.
/// </summary>
public sealed class WebHostLedgerTests
{
    [Fact]
    public void Remember_FindsEveryAddressInProse()
    {
        var ledger = new WebHostLedger();

        ledger.Remember("先看了 https://example.com/a/b?q=1，又看了 http://docs.example.test/x，就这样。");

        Assert.Equal(new[] { "docs.example.test", "example.com" }, ledger.Hosts);
        Assert.True(ledger.Saw("example.com"));
        Assert.True(ledger.Saw("docs.example.test"));
    }

    [Fact]
    public void Remember_DoesNotTreatATrailingFullStopAsPartOfTheHost()
    {
        // A URL at the end of an English sentence is the common case, and the dot is the sentence's.
        var ledger = new WebHostLedger();

        ledger.Remember("See https://example.com. Thanks.");

        Assert.Equal(new[] { "example.com" }, ledger.Hosts);
        Assert.False(ledger.Saw("example.com."));
    }

    [Fact]
    public void Remember_IgnoresAnythingThatIsNotAnHttpAddress()
    {
        var ledger = new WebHostLedger();

        ledger.Remember("file:///C:/Windows/System32/config/SAM and ftp://example.com/x and just some words");

        Assert.Empty(ledger.Hosts);
    }

    [Fact]
    public void Remember_IgnoresARepeatedHost()
    {
        var ledger = new WebHostLedger();

        ledger.Remember("https://example.com/a");
        ledger.Remember("https://example.com/b");

        Assert.Equal(new[] { "example.com" }, ledger.Hosts);
    }

    [Fact]
    public void Remember_AndSawIgnoreCase()
    {
        var ledger = new WebHostLedger();

        ledger.Remember("https://Example.COM/a");

        Assert.True(ledger.Saw("example.com"));
        Assert.True(ledger.Saw("EXAMPLE.COM"));

        // A hostname is not case-sensitive, and neither is a person typing one.
        ledger.Add("Docs.Example.Test");
        Assert.True(ledger.Saw("docs.example.test"));
    }

    [Fact]
    public void Add_AndRememberAgreeAboutWhatAHostIs()
    {
        var ledger = new WebHostLedger();

        ledger.Add("example.com");
        ledger.Remember("https://example.com/a");

        Assert.Equal(new[] { "example.com" }, ledger.Hosts);
    }

    [Fact]
    public void Add_EvictsTheOldestHostAtCapacity()
    {
        var ledger = new WebHostLedger();

        for (var index = 0; index <= WebHostLedger.Capacity; index++)
        {
            ledger.Add($"host{index}.test");
        }

        Assert.Equal(WebHostLedger.Capacity, ledger.Hosts.Count);
        Assert.False(ledger.Saw("host0.test"));
        Assert.True(ledger.Saw($"host{WebHostLedger.Capacity}.test"));
    }

    [Fact]
    public void Saw_IsFalseForNothingAndForAnUnknownHost()
    {
        var ledger = new WebHostLedger();
        ledger.Add("example.com");

        Assert.False(ledger.Saw(null));
        Assert.False(ledger.Saw(string.Empty));
        Assert.False(ledger.Saw("unknown.test"));
    }

    [Fact]
    public void Add_IgnoresNothing()
    {
        var ledger = new WebHostLedger();

        ledger.Add(null);
        ledger.Add("   ");

        Assert.Empty(ledger.Hosts);
    }
}
