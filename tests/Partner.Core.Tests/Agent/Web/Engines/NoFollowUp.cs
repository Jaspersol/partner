using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// A second request that fails the test if it is ever made. Every engine except the weather lookup reads
/// everything it needs from the one response it was handed, so a parser that reaches for another one has changed
/// behaviour — and saying so here beats asserting whatever that request happened to come back with.
/// </summary>
internal sealed class NoFollowUp : IEngineFollowUp
{
    public Task<(string? Body, string? Error)> GetAsync(Uri url, CancellationToken ct = default) =>
        throw new InvalidOperationException($"the parser made a second request, to {url}");
}
