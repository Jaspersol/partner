namespace Partner.Core.Agent.Web;

/// <summary>What the gate decided about one engine's batch, and why.</summary>
/// <param name="Engine">The engine.</param>
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
/// An engine that has started serving challenges still returns HTTP 200 with a page in it, and an engine that has
/// quietly stopped indexing the subject still returns a full batch of results — just not about the subject. Both
/// reach the model as "eight results" unless something looks at the shape of the batch rather than the status
/// code, which is what this does.
/// </para>
/// <para>
/// The decoy test is deliberately batch-level and never per-result. A single irrelevant result is ordinary; a
/// whole batch that mentions nothing the user asked about is a served answer, and only the batch can tell them
/// apart.
/// </para>
/// </summary>
public sealed class BatchGate
{
    /// <summary>
    /// Below this share of the batch echoing the query, the batch is presumed served rather than found.
    /// <para>
    /// PROVISIONAL. The plan is explicit that this number must come from replaying archived responses and not
    /// from a guess, and no archived responses exist yet — this machine cannot reach the engines to capture any.
    /// It is set at the bottom of the gap the two distributions are expected to leave, and the audit tool
    /// (<c>web-audit</c>, to be built with the fixtures) is what must recalibrate it before the number is
    /// trusted. Until then it can only under-call, never over-call: see <see cref="CompanionFloor"/>.
    /// </para>
    /// </summary>
    public const double DecoyEchoCeiling = 0.15;

    /// <summary>
    /// What a batch must score to count as the control that unmasks another batch. A batch with no companion
    /// like this is left alone: "the English question answered by a foreign-language source" is a legitimate
    /// batch with a low echo, and calling it a decoy would throw away real results.
    /// </summary>
    public const double CompanionFloor = 0.4;

    /// <summary>Batches smaller than this are not judged on echo at all — the ratio means nothing at n=1.</summary>
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
                // Empty and "Ok but parsed to nothing" are the same thing downstream; the distinction that
                // matters is whether a companion found something, and that is the health store's question.
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

                // Every clause is needed. Small batches are not judged; a low echo alone is not enough without a
                // healthy batch to compare against; and the ratio must be under the ceiling.
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
