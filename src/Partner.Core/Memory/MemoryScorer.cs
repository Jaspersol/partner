using System.Globalization;
using System.Text;

namespace Partner.Core.Memory;

/// <summary>Produces an explainable <see cref="MemoryScore"/> for a retrieval candidate.</summary>
public interface IMemoryScorer
{
    MemoryScore Score(MemoryCandidate candidate, string query, float[]? queryEmbedding, float[]? candidateEmbedding, DateTimeOffset now);

    /// <summary>Human-readable lines for the non-zero components of a score, in a fixed order.</summary>
    IReadOnlyList<string> Explain(MemoryScore score);
}

/// <summary>
/// Pure scoring of retrieval candidates (design doc §2.5.6 ④/⑤). Every component is normalised to
/// <c>[0,1]</c>; <see cref="MemoryScore.Total"/> folds them with <see cref="MemoryScorerOptions"/> weights
/// and applies the content penalty last.
/// </summary>
public sealed class MemoryScorer : IMemoryScorer
{
    private const int MinTokenLength = 2;
    private const int MinUsefulBodyLength = 8;
    private const double ShortBodyPenalty = 0.5;
    private const double JunkBodyPenalty = 0.25;

    private readonly MemoryScorerOptions _options;

    public MemoryScorer(MemoryScorerOptions? options = null) => _options = options ?? new MemoryScorerOptions();

    public MemoryScore Score(MemoryCandidate candidate, string query, float[]? queryEmbedding, float[]? candidateEmbedding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        double lexical = ComputeLexical(query, candidate.Body);
        double vector = ComputeVector(queryEmbedding, candidateEmbedding);
        double fts = ComputeFts(candidate.FtsRank);
        double importance = Clamp01(candidate.Importance);
        double temporal = ComputeTemporalDecay(candidate.CreatedAt, now);
        double veracity = Clamp01(candidate.Veracity);
        double tier = TierWeight(candidate.Tier);
        double content = ComputeContentAdjustment(candidate.Body);

        return new MemoryScore(lexical, vector, fts, importance, temporal, veracity, tier, content)
        {
            Weights = _options,
        };
    }

    public IReadOnlyList<string> Explain(MemoryScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        var reasons = new List<string>(8);
        AddIfNonZero(reasons, "lexical", score.Lexical);
        AddIfNonZero(reasons, "vector", score.Vector);
        AddIfNonZero(reasons, "fts", score.Fts);
        AddIfNonZero(reasons, "importance", score.Importance);
        AddIfNonZero(reasons, "temporal", score.TemporalDecay);
        AddIfNonZero(reasons, "veracity", score.Veracity);
        AddIfNonZero(reasons, "tier", score.Tier);

        // A fully-zero score has nothing to explain, penalty or not.
        if (reasons.Count == 0)
        {
            return reasons;
        }

        if (score.ContentAdjustment < 1.0)
        {
            reasons.Add($"content penalty x{FormatMultiplier(score.ContentAdjustment)}");
        }

        return reasons;
    }

    /// <summary>Token-overlap between query and body: <c>|query ∩ body| / |query|</c>, case-insensitive.</summary>
    private static double ComputeLexical(string? query, string? body)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
        {
            return 0.0;
        }

        var bodyTokens = Tokenize(body);
        int overlap = 0;
        foreach (string token in queryTokens)
        {
            if (bodyTokens.Contains(token))
            {
                overlap++;
            }
        }

        return (double)overlap / queryTokens.Count;
    }

    /// <summary>Cosine mapped from <c>[-1,1]</c> to <c>[0,1]</c>; zero unless both embeddings are present.</summary>
    private static double ComputeVector(float[]? queryEmbedding, float[]? candidateEmbedding)
    {
        if (queryEmbedding is null || candidateEmbedding is null)
        {
            return 0.0;
        }

        return (CosineSimilarity.Compute(queryEmbedding, candidateEmbedding) + 1.0) / 2.0;
    }

    /// <summary>
    /// bm25() is negative, and the more negative it is the better the match — so the component grows with the
    /// rank's magnitude. A null rank means the row did not come from an index and scores zero; a zero rank is a
    /// real match that happens to weigh nothing, which is why "unused" is not spelled as zero.
    /// </summary>
    private static double ComputeFts(double? ftsRank)
    {
        if (ftsRank is not { } rank || double.IsNaN(rank))
        {
            return 0.0;
        }

        var magnitude = Math.Abs(rank);
        return magnitude / (1.0 + magnitude);
    }

    /// <summary>Exponential decay with the configured half-life; future timestamps do not decay.</summary>
    private double ComputeTemporalDecay(DateTimeOffset createdAt, DateTimeOffset now)
    {
        TimeSpan halfLife = _options.TemporalHalfLife;
        if (halfLife <= TimeSpan.Zero)
        {
            return 1.0;
        }

        TimeSpan age = now - createdAt;
        if (age <= TimeSpan.Zero)
        {
            return 1.0;
        }

        return Math.Pow(0.5, age.TotalSeconds / halfLife.TotalSeconds);
    }

    /// <summary>Penalty multiplier that keeps the model honest about junk bodies.</summary>
    private static double ComputeContentAdjustment(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || IsSingleRepeatedCharacter(body))
        {
            return JunkBodyPenalty;
        }

        return body.Length < MinUsefulBodyLength ? ShortBodyPenalty : 1.0;
    }

    private static bool IsSingleRepeatedCharacter(string body)
    {
        char first = body[0];
        for (int i = 1; i < body.Length; i++)
        {
            if (body[i] != first)
            {
                return false;
            }
        }

        return true;
    }

    private static double TierWeight(MemoryTier tier) => tier switch
    {
        MemoryTier.Working => 0.4,
        MemoryTier.Episodic => 0.7,
        MemoryTier.Semantic => 1.0,
        MemoryTier.Procedural => 1.0,
        _ => 0.5,
    };

    private static HashSet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                continue;
            }

            Flush(tokens, builder);
        }

        Flush(tokens, builder);
        return tokens;
    }

    private static void Flush(HashSet<string> tokens, StringBuilder builder)
    {
        var run = builder.ToString();
        builder.Clear();

        if (!run.Any(CjkText.IsCjk))
        {
            if (run.Length >= MinTokenLength)
            {
                tokens.Add(run);
            }

            return;
        }

        // A run of Chinese has no spaces to split on, so as one token it matches only a sentence worded exactly
        // the same. Its character pairs are what two differently-worded sentences can share (see CjkText); the
        // Latin or digit stretches around it stay whole tokens.
        foreach (var bigram in CjkText.Bigrams(run))
        {
            tokens.Add(bigram);
        }

        var latin = new StringBuilder();
        foreach (var ch in run)
        {
            if (CjkText.IsCjk(ch))
            {
                AddLatin(tokens, latin);
                continue;
            }

            latin.Append(ch);
        }

        AddLatin(tokens, latin);

        static void AddLatin(HashSet<string> tokens, StringBuilder latin)
        {
            if (latin.Length >= MinTokenLength)
            {
                tokens.Add(latin.ToString());
            }

            latin.Clear();
        }
    }

    private static void AddIfNonZero(List<string> reasons, string name, double value)
    {
        if (value > 0.0)
        {
            reasons.Add($"{name} {Format(value)}");
        }
    }

    private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatMultiplier(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0.0;
        }

        if (value <= 0.0)
        {
            return 0.0;
        }

        return value >= 1.0 ? 1.0 : value;
    }
}
