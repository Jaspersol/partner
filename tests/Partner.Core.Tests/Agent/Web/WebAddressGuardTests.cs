using System.Net;
using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The address check, which is the one thing this capability adds that nothing else in the repository covers:
/// the CDP debug port, this API and the voice engines all listen on loopback, so a URL read out of a fetched page
/// must never reach them.
/// </summary>
public sealed class WebAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // the loopback the debug port is on
    [InlineData("127.1.2.3")]      // the whole 127/8, not just the address everybody writes
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // the cloud metadata service
    [InlineData("100.64.0.1")]      // carrier-grade NAT
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]         // unique-local, the IPv6 spelling of a private network
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped: reaches the same place as 127.0.0.1
    public void IsBlocked_RefusesThisMachineAndThisNetwork(string address) =>
        Assert.True(WebAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]  // just below the private block
    [InlineData("172.32.0.1")]  // just above it
    [InlineData("100.63.0.1")]  // just below CGNAT
    [InlineData("100.128.0.1")] // just above it
    [InlineData("2606:4700:4700::1111")]
    public void IsBlocked_AllowsPublicAddresses(string address) =>
        Assert.False(WebAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM", "http/https")]
    [InlineData("ftp://example.com/x", "http/https")]
    [InlineData("http://user:pass@example.com/", "账号密码")]
    [InlineData("http://localhost/x", "不是公网域名")]
    [InlineData("http://build-server/x", "不是公网域名")] // a NetBIOS or container name
    public void Reject_RefusesByShape(string url, string expected)
    {
        var reason = WebAddressGuard.Reject(new Uri(url));

        Assert.NotNull(reason);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Reject_AllowsAnOrdinaryPublicAddress() =>
        Assert.Null(WebAddressGuard.Reject(new Uri("https://example.com/a/b?c=d")));

    [Fact]
    public async Task Resolve_RefusesALoopbackLiteralWithoutAskingDns()
    {
        // Judged from the text of the URL: a hostname that resolves to a public address today can resolve to a
        // private one tomorrow, which is the window the whole guard exists to close.
        var (allowed, error) = await WebAddressGuard.ResolveAsync(new Uri("http://127.0.0.1:9222/json"));

        Assert.Empty(allowed);
        Assert.NotNull(error);
        Assert.Contains("本机或内网", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ReturnsTheCheckedAddressForAPublicLiteral()
    {
        var (allowed, error) = await WebAddressGuard.ResolveAsync(new Uri("http://8.8.8.8/"));

        Assert.Null(error);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(allowed));
    }

    [Fact]
    public async Task Resolve_ReportsAStructuralRefusalWithoutDns()
    {
        var (allowed, error) = await WebAddressGuard.ResolveAsync(new Uri("http://localhost/"));

        Assert.Empty(allowed);
        Assert.Contains("不是公网域名", error!, StringComparison.Ordinal);
    }
}
