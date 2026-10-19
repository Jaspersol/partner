using System.Net;
using System.Net.Sockets;

namespace Partner.Core.Agent.Web;

/// <summary>
/// What a public web request may not reach: anything on this machine or inside this network.
/// <para>
/// This is the one attack surface the network capability adds that nothing else in the repository covers. The
/// desktop CDP debug port, the API itself and the voice engines all listen on loopback, so a model that reads
/// "fetch http://127.0.0.1:9222/json" in a page it just fetched would be handed this machine's own control
/// surfaces. The check therefore runs on <b>every hop</b>, including redirects, and the connection is made to
/// the address that was checked — resolving again at connect time would open the DNS-rebinding window that
/// makes the whole guard decorative.
/// </para>
/// </summary>
public static class WebAddressGuard
{
    /// <summary>Only these schemes go out. <c>file:</c> and credentials in the URL are refused by name.</summary>
    public static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Whether an address is off limits: loopback, private, link-local (which is where the cloud metadata
    /// service at 169.254.169.254 lives), carrier-grade NAT, multicast, unspecified, and IPv6 addresses that
    /// wrap any of those.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4-mapped IPv6 address (::ffff:127.0.0.1) reaches the same place as the IPv4 address, so it has
        // to be judged as that IPv4 address rather than by its own IPv6 ranges.
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlocked(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            {
                return true;
            }

            // ::  is "unspecified", fc00::/7 is unique-local — the IPv6 spelling of a private network.
            var v6 = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None)
                || (v6[0] & 0xFE) == 0xFC;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => true,                                  // 0.0.0.0/8 — "this network"
            10 => true,                                 // private
            127 => true,                                // loopback
            169 when octets[1] == 254 => true,          // link-local, incl. 169.254.169.254
            172 when octets[1] is >= 16 and <= 31 => true, // private
            192 when octets[1] == 168 => true,          // private
            100 when octets[1] is >= 64 and <= 127 => true, // carrier-grade NAT
            >= 224 => true,                             // multicast and the reserved top of the range
            _ => false,
        };
    }

    /// <summary>
    /// The first structural reason a URL may not be fetched, or null when it is acceptable so far. Says nothing
    /// about DNS: see <see cref="ResolveAsync"/>, which is where an address is actually judged.
    /// </summary>
    public static string? Reject(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!IsAllowedScheme(uri))
        {
            return $"只支持 http/https，不接受 {uri.Scheme}:";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "地址里带了账号密码，不接受";
        }

        // A host with no dot is a NetBIOS name or a container name; either way it is not the public web, and it
        // is exactly how "http://localhost" reaches the machine without ever spelling out 127.0.0.1.
        if (string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            return $"主机名 '{uri.Host}' 不是公网域名";
        }

        return null;
    }

    /// <summary>
    /// Resolves a host and returns the addresses that are allowed, or a reason why none are. An empty allow-list
    /// is a refusal: a target that resolves only to blocked addresses must not be reached by ignoring the
    /// blocked ones.
    /// </summary>
    public static async Task<(IReadOnlyList<IPAddress> Allowed, string? Error)> ResolveAsync(Uri uri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (Reject(uri) is { } structural)
        {
            return ([], structural);
        }

        // An address written out in the URL is judged without asking DNS at all — and asking would be wrong,
        // because a hostname that resolves to a public address today can resolve to a private one tomorrow.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
        {
            return IsBlocked(literal)
                ? ([], $"'{uri.Host}' 是本机或内网地址，拒绝访问")
                : ([literal], null);
        }

        IPAddress[] resolved;
        try
        {
            resolved = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
        }
        catch (SocketException error)
        {
            return ([], $"unreachable: 域名解析失败（{error.Message}）");
        }

        var allowed = resolved.Where(address => !IsBlocked(address)).ToArray();
        if (allowed.Length == 0)
        {
            return ([], resolved.Length == 0
                ? $"unreachable: 域名 {uri.Host} 没有解析出任何地址"
                : $"'{uri.Host}' 解析到本机或内网地址（{string.Join(", ", resolved.Select(a => a.ToString()))}），拒绝访问");
        }

        return (allowed, null);
    }
}
