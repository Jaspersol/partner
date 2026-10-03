namespace Partner.Core.Memory;

/// <summary>Memory tier of a record (design doc §2.5.6).</summary>
public enum MemoryTier
{
    Working,
    Episodic,
    Semantic,
    Procedural,
}

/// <summary>Retention/recall scope of a record (design doc §2.5.6).</summary>
public enum MemoryScope
{
    Retain,
    Recall,
    Global,
}

/// <summary>A retrieval candidate as loaded from the store (no body rewriting here).</summary>
public sealed record MemoryCandidate(
    string RecordId,
    string Body,
    MemoryTier Tier,
    MemoryScope Scope,
    DateTimeOffset CreatedAt,
    double Importance,      // 0..1
    double Veracity,        // 0..1
    double Confidence,      // 0..1
    double FtsRank);        // lower = better, as returned by SQLite bm25(); may be 0 when unused

/// <summary>
/// Explainable score of a candidate: every component is in <c>[0,1]</c> and is reported separately so
/// the retrieval path can say <em>why</em> a record surfaced.
/// </summary>
public sealed record MemoryScore(
    double Lexical,
    double Vector,
    double Fts,
    double Importance,
    double TemporalDecay,
    double Veracity,
    double Tier,
    double ContentAdjustment)
{
    private static readonly MemoryScorerOptions DefaultWeights = new();

    /// <summary>Weights used to fold the components into <see cref="Total"/>; set by the scorer.</summary>
    internal MemoryScorerOptions Weights { get; init; } = DefaultWeights;

    /// <summary>
    /// Weighted sum of the components (weights live in <see cref="MemoryScorerOptions"/>), scaled by the
    /// <see cref="ContentAdjustment"/> penalty multiplier.
    /// </summary>
    public double Total
    {
        get
        {
            double weighted =
                (Lexical * Weights.WeightLexical) +
                (Vector * Weights.WeightVector) +
                (Fts * Weights.WeightFts) +
                (Importance * Weights.WeightImportance) +
                (TemporalDecay * Weights.WeightTemporal) +
                (Veracity * Weights.WeightVeracity) +
                (Tier * Weights.WeightTier);

            // ContentAdjustment is a penalty multiplier applied to the weighted sum, not a component of it.
            return weighted * ContentAdjustment;
        }
    }
}

/// <summary>
/// A scored, explainable retrieval hit.
/// <para>
/// Carries when it was recorded and which tier it came from, because a hook that injects it into a prompt has
/// to be able to say how old it is. "The user prefers dark mode" read as fact is different from the same line
/// read as something from three months ago that may since have changed — and the model can only weigh that if
/// it is told.
/// </para>
/// </summary>
public sealed record MemoryHit(
    string RecordId,
    string Body,
    MemoryScore Score,
    IReadOnlyList<string> Reasons,
    DateTimeOffset CreatedAt,
    MemoryTier Tier,
    DateTimeOffset Now);

/// <summary>How old a recalled memory is, in the words a reader would use.</summary>
public static class MemoryAge
{
    /// <summary>
    /// A short phrase for a record's age, relative to <paramref name="now"/>.
    /// <para>
    /// Deliberately vague past a month. "42 days ago" invites the model to reason about a day count it cannot
    /// use, whereas "recorded a while ago" correctly signals that this is background rather than a fact the
    /// person just stated.
    /// </para>
    /// </summary>
    public static string Describe(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var age = now - createdAt;

        if (age < TimeSpan.FromHours(1))
        {
            return "刚刚";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)age.TotalHours)} 小时前";
        }

        if (age < TimeSpan.FromDays(30))
        {
            return $"{Math.Max(1, (int)age.TotalDays)} 天前";
        }

        return "很久以前";
    }
}

/// <summary>Tunables of <see cref="MemoryScorer"/>.</summary>
public sealed record MemoryScorerOptions
{
    public double WeightLexical { get; init; } = 0.30;
    public double WeightVector { get; init; } = 0.25;
    public double WeightFts { get; init; } = 0.15;
    public double WeightImportance { get; init; } = 0.10;
    public double WeightTemporal { get; init; } = 0.10;
    public double WeightVeracity { get; init; } = 0.05;
    public double WeightTier { get; init; } = 0.05;

    /// <summary>Half-life for temporal decay.</summary>
    public TimeSpan TemporalHalfLife { get; init; } = TimeSpan.FromDays(30);
}
