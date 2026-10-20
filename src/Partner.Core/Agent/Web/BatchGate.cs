namespace Partner.Core.Agent.Web;

/// <summary>What the gate decided about one engine's batch, and why.</summary>
/// <param name="Keep">Whether its hits go on to the merger.</param>
/// <param name="Rejection">The category when it does not: <c>blocked</c>, <c>decoy</c>, <c>empty</c>.</param>
/// <param name="Echo">The share of the batch that mentions the query.</param>
/// <param name="Hits">Its hits, kept for the audit tool even when they are discarded.</param>
public sealed record BatchVerdict(
    IWebSearchEngine Engine,
    bool Keep,
    string? Rejection,
    double Echo,
    IReadOnlyList<SearchHit> Hits);

/// <summary>The gate's output: the surviving hits, and every batch's fate.</summary>
public sealed record GateOutcome(IReadOnlyList<SearchHit> Hits, IReadOnlyList<BatchVerdict> Batches)
{
    /// <summary>Whether any batch was allowed through — the control the silent-empty rule asks for.</summary>
    public bool AnyKept => Batches.Any(batch => batch.Keep);
}

/// <summary>
/// The second quality gate: telling a failure that looks like a success from a success.
/// <para>
/// A challenged engine still returns HTTP 200 with a page in it, and an engine that has stopped indexing the
/// subject still returns a full batch of results, just not about the subject. Both reach the model as "eight
/// results" unless the shape of the batch is checked.
/// </para>
/// <para>
/// The decoy test is deliberately batch-level and never per-result: one irrelevant result is ordinary, but a
/// whole batch that mentions nothing the user asked about is a served answer.
/// </para>
/// </summary>
public sealed class BatchGate
{
    /// <summary>
    /// Below this share of the batch echoing the query, the batch is presumed served rather than found.
    /// <para>
    /// PROVISIONAL: must be recalibrated from archived responses by <c>web-audit</c> before it is trusted.
    /// Until then it can only under-call, never over-call: see <see cref="CompanionFloor"/>.
    /// </para>
    /// </summary>
    public const double DecoyEchoCeiling = 0.15;

    /// <summary>
    /// What a batch must score to count as the control that unmasks another batch. A batch with no companion
    /// like this is left alone: "the English question answered by a foreign-language source" is a legitimate
    /// batch with a low echo, and calling it a decoy would throw away real results.
    /// </summary>
    public const double CompanionFloor = 0.4;

    /// <summary>Batches smaller than this are not judged on echo — the ratio means nothing at n=1.</summary>
    public const int MinBatchForDecoyCheck = 3;

    public GateOutcome Apply(IReadOnlyList<EngineAttempt> attempts, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(query);

        var tokens = QueryTokens.Of(query.Text);
        var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
        var verdicts = new List<BatchVerdict>();

        foreach (var attempt in attempts)
        {
            var hits = attempt.Hits;

            if (attempt.Result?.Status == ParseStatus.Blocked)
            {
                verdicts.Add(new BatchVerdict(attempt.Engine, false, "blocked", 0, []));
                continue;
            }

            if (hits.Count == 0)
            {
                // Empty and "Ok but parsed to nothing" are the same downstream; the distinction that matters
                // is whether a companion found something, which is the health store's question.
                verdicts.Add(new BatchVerdict(attempt.Engine, false, "empty", 0, []));
                continue;
            }

            var traits = attempt.Engine.Traits;
            var echo = traits.SingleSite ? 1 : QueryTokens.EchoRatio(hits, tokens);
            verdicts.Add(new BatchVerdict(attempt.Engine, true, null, echo, hits));
            ratios[traits.Id] = echo;
        }

        var control = ratios.Values.Any(value => value >= CompanionFloor);

        var decided = verdicts
            .Select(verdict =>
            {
                if (!verdict.Keep || verdict.Engine.Traits.SingleSite)
                {
                    return verdict;
                }

                var sizeable = verdict.Hits.Count >= MinBatchForDecoyCheck;

                // Every clause is needed: small batches are not judged, a low echo alone is not enough without a
                // healthy batch to compare against, and the ratio must be under the ceiling.
                var decoy = sizeable && control && verdict.Echo < DecoyEchoCeiling;

                return decoy ? verdict with { Keep = false, Rejection = "decoy" } : verdict;
            })
            .ToList();

        var kept = decided
            .Where(verdict => verdict.Keep)
            .SelectMany(verdict => verdict.Hits)
            .ToList();

        return new GateOutcome(kept, decided);
    }
}
