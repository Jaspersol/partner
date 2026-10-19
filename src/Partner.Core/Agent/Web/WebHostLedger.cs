using System.Text.RegularExpressions;

namespace Partner.Core.Agent.Web;

/// <summary>
/// The hosts this session has already seen: the ones its searches returned, and the ones a person wrote in a
/// message.
/// <para>
/// This exists so <c>web_fetch</c> can tell "open this result you just found" from "go and fetch this address you
/// produced yourself". The first is the ordinary flow and costs the user nothing; the second is the model picking
/// a destination, which is a decision the user gets to make — see the governance branch that turns an unseen host
/// into an approval request.
/// </para>
/// <para>
/// Bounded, and deliberately not persisted: it answers "in this session" and nothing more. A host seen a week ago
/// says nothing about whether today's link is one the person meant to visit.
/// </para>
/// </summary>
public sealed partial class WebHostLedger
{
    /// <summary>Hosts remembered. Far past what a conversation mentions, so the bound is about not growing forever.</summary>
    public const int Capacity = 512;

    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>Remembers every URL host in a piece of text — a search result list, or what the person typed.</summary>
    public void Remember(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in UrlPattern().Matches(text))
        {
            // Only http(s), and only what Uri itself accepts: a malformed run of characters is not evidence the
            // host was seen, and remembering a truncated host would be worse than remembering nothing.
            if (!Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ':', '!', '?'), UriKind.Absolute, out var uri))
            {
                continue;
            }

            Add(uri.Host);
        }
    }

    /// <summary>Remembers one host by name, for results that arrive already parsed.</summary>
    public void Add(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        lock (_gate)
        {
            if (!_hosts.Add(host))
            {
                return;
            }

            _order.Enqueue(host);
            while (_order.Count > Capacity)
            {
                _hosts.Remove(_order.Dequeue());
            }
        }
    }

    public bool Saw(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Locked(host);

    private bool Locked(string host)
    {
        lock (_gate)
        {
            return _hosts.Contains(host);
        }
    }

    /// <summary>What is in the ledger, for tests and for the settings page's diagnostics.</summary>
    public IReadOnlyList<string> Hosts
    {
        get
        {
            lock (_gate)
            {
                return _hosts.OrderBy(host => host, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>
    /// Anything that looks like an absolute http(s) URL. Intentionally crude: it runs over prose a person typed
    /// and over text a search engine wrote, and it is followed by a real <see cref="Uri"/> parse, so a false
    /// positive costs one rejected string rather than a wrong host.
    /// </summary>
    [GeneratedRegex(@"https?://[^\s""'<>()\[\]{}、。，；）】]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
