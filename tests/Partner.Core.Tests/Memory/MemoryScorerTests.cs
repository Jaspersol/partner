using Partner.Core.Memory;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// Scoring semantics of the memory layer (design doc §2.5.6 ④/⑤): every component is normalised,
/// the content penalty is a multiplier, and every hit can explain itself.
/// </summary>
public sealed class MemoryScorerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static MemoryCandidate Candidate(
        string body,
        MemoryTier tier = MemoryTier.Semantic,
        double importance = 0.5,
        double veracity = 1.0,
        double? ftsRank = null,
        DateTimeOffset? createdAt = null) =>
        new("rec-1", body, tier, MemoryScope.Recall, createdAt ?? Now, importance, veracity, 1.0, ftsRank);

    private static MemoryScore Score(
        MemoryCandidate candidate,
        string query = "",
        float[]? queryEmbedding = null,
        float[]? candidateEmbedding = null,
        MemoryScorerOptions? options = null) =>
        new MemoryScorer(options).Score(candidate, query, queryEmbedding, candidateEmbedding, Now);

    [Fact]
    public void Lexical_is_the_share_of_query_tokens_found_in_the_body()
    {
        MemoryScore full = Score(Candidate("alpha beta gamma"), "alpha beta");
        Assert.Equal(1.0, full.Lexical, 10);

        MemoryScore none = Score(Candidate("gamma"), "alpha beta");
        Assert.Equal(0.0, none.Lexical, 10);

        MemoryScore partial = Score(Candidate("alpha delta"), "alpha beta");
        Assert.Equal(0.5, partial.Lexical, 10);
    }

    [Fact]
    public void Differently_worded_chinese_sentences_share_lexical_overlap_through_their_character_pairs()
    {
        // 我不太喜欢被打断 and 用户不喜欢被反复确认 share no whole token, but they share 喜欢 and 不喜 ... pairs.
        MemoryScore related = Score(Candidate("用户不喜欢被反复确认"), "我不太喜欢被打断");
        Assert.True(related.Lexical > 0.0, "a reworded sentence should still overlap");

        MemoryScore unrelated = Score(Candidate("今天下午三点开会"), "我不太喜欢被打断");
        Assert.Equal(0.0, unrelated.Lexical, 10);

        // A Latin word inside a Chinese sentence is still a token of its own.
        Assert.True(Score(Candidate("部署 deployment 已完成"), "deployment 状态").Lexical > 0.0);
    }

    [Fact]
    public void Lexical_ignores_case_and_tokens_shorter_than_two_characters()
    {
        Assert.Equal(1.0, Score(Candidate("Alpha BETA gamma"), "alpha beta").Lexical, 10);
        Assert.Equal(0.0, Score(Candidate("alpha beta gamma"), "a").Lexical, 10);
        Assert.Equal(0.0, Score(Candidate("alpha beta gamma"), "!!").Lexical, 10);
        Assert.Equal(0.0, Score(Candidate("alpha beta gamma"), "").Lexical, 10);
    }

    [Fact]
    public void Temporal_decay_follows_the_configured_half_life()
    {
        var options = new MemoryScorerOptions { TemporalHalfLife = TimeSpan.FromDays(30) };

        Assert.Equal(1.0, Score(Candidate("abcdefgh", createdAt: Now), options: options).TemporalDecay, 6);
        Assert.Equal(1.0, Score(Candidate("abcdefgh", createdAt: Now - TimeSpan.FromTicks(1)), options: options).TemporalDecay, 6);
        Assert.Equal(0.5, Score(Candidate("abcdefgh", createdAt: Now - TimeSpan.FromDays(30)), options: options).TemporalDecay, 2);
        Assert.Equal(0.25, Score(Candidate("abcdefgh", createdAt: Now - TimeSpan.FromDays(60)), options: options).TemporalDecay, 2);
    }

    [Fact]
    public void Temporal_decay_does_not_penalise_future_timestamps()
    {
        Assert.Equal(1.0, Score(Candidate("abcdefgh", createdAt: Now + TimeSpan.FromDays(7))).TemporalDecay, 6);
    }

    [Fact]
    public void Tier_weight_orders_semantic_over_episodic_over_working()
    {
        double semantic = Score(Candidate("alpha beta gamma delta", MemoryTier.Semantic)).Total;
        double procedural = Score(Candidate("alpha beta gamma delta", MemoryTier.Procedural)).Total;
        double episodic = Score(Candidate("alpha beta gamma delta", MemoryTier.Episodic)).Total;
        double working = Score(Candidate("alpha beta gamma delta", MemoryTier.Working)).Total;

        Assert.Equal(1.0, Score(Candidate("abcdefgh", MemoryTier.Semantic)).Tier, 10);
        Assert.Equal(1.0, Score(Candidate("abcdefgh", MemoryTier.Procedural)).Tier, 10);
        Assert.Equal(0.7, Score(Candidate("abcdefgh", MemoryTier.Episodic)).Tier, 10);
        Assert.Equal(0.4, Score(Candidate("abcdefgh", MemoryTier.Working)).Tier, 10);

        Assert.True(semantic > episodic);
        Assert.True(episodic > working);
        Assert.Equal(semantic, procedural, 10);
    }

    [Fact]
    public void Content_penalty_halves_the_total_of_a_short_body()
    {
        MemoryScore shortBody = Score(Candidate("abc"), "zzz");
        MemoryScore longBody = Score(Candidate("abcdefgh"), "zzz");

        Assert.Equal(0.5, shortBody.ContentAdjustment, 10);
        Assert.Equal(1.0, longBody.ContentAdjustment, 10);
        Assert.Equal(0.5, shortBody.Total / longBody.Total, 2);
    }

    [Fact]
    public void Content_penalty_quarters_whitespace_only_and_single_repeated_character_bodies()
    {
        Assert.Equal(0.25, Score(Candidate("aaaaaaaa")).ContentAdjustment, 10);
        Assert.Equal(0.25, Score(Candidate("   ")).ContentAdjustment, 10);
        Assert.Equal(0.25, Score(Candidate("")).ContentAdjustment, 10);
        Assert.Equal(1.0, Score(Candidate("abcdabcd")).ContentAdjustment, 10);
        Assert.Equal(1.0, Score(Candidate("abcdefgh")).ContentAdjustment, 10);
    }

    [Fact]
    public void Fts_is_zero_without_an_index_and_grows_with_the_bm25_magnitude()
    {
        // null is "this row did not come from an index"; zero is a real match that weighs nothing. Both score
        // zero, and neither is the same thing as a negative rank — those are the actual bm25 output.
        Assert.Equal(0.0, Score(Candidate("abcdefgh", ftsRank: null)).Fts, 10);
        Assert.Equal(0.0, Score(Candidate("abcdefgh", ftsRank: 0.0)).Fts, 10);
        Assert.Equal(0.5, Score(Candidate("abcdefgh", ftsRank: -1.0)).Fts, 10);
        Assert.Equal(2.0 / 3.0, Score(Candidate("abcdefgh", ftsRank: -2.0)).Fts, 10);
        Assert.Equal(3.5 / 4.5, Score(Candidate("abcdefgh", ftsRank: -3.5)).Fts, 10);
    }

    [Fact]
    public void Fts_grows_monotonically_with_the_strength_of_the_match()
    {
        double weak = Score(Candidate("abcdefgh", ftsRank: -0.2)).Fts;
        double middle = Score(Candidate("abcdefgh", ftsRank: -1.0)).Fts;
        double strong = Score(Candidate("abcdefgh", ftsRank: -4.0)).Fts;

        Assert.True(weak > 0.0);
        Assert.True(weak < middle);
        Assert.True(middle < strong);
        Assert.True(strong < 1.0);
    }

    [Fact]
    public void Explain_reports_fts_as_a_reason()
    {
        var scorer = new MemoryScorer();
        MemoryScore score = scorer.Score(
            Candidate("alpha beta gamma", importance: 0.0, veracity: 0.0, ftsRank: -1.0),
            "alpha beta",
            null,
            null,
            Now);

        Assert.Equal(new[] { "lexical 1.00", "fts 0.50", "temporal 1.00", "tier 1.00" }, scorer.Explain(score));
    }

    [Fact]
    public void Importance_and_veracity_are_clamped_to_the_unit_interval()
    {
        MemoryScore high = Score(Candidate("abcdefgh", importance: 4.0, veracity: 2.5));
        Assert.Equal(1.0, high.Importance, 10);
        Assert.Equal(1.0, high.Veracity, 10);

        MemoryScore low = Score(Candidate("abcdefgh", importance: -1.0, veracity: -0.5));
        Assert.Equal(0.0, low.Importance, 10);
        Assert.Equal(0.0, low.Veracity, 10);
    }

    [Fact]
    public void Vector_component_requires_both_embeddings()
    {
        float[] unit = [1f, 0f, 0f];

        Assert.Equal(0.0, Score(Candidate("abcdefgh"), queryEmbedding: null, candidateEmbedding: unit).Vector, 10);
        Assert.Equal(0.0, Score(Candidate("abcdefgh"), queryEmbedding: unit, candidateEmbedding: null).Vector, 10);

        Assert.Equal(1.0, Score(Candidate("abcdefgh"), queryEmbedding: unit, candidateEmbedding: [1f, 0f, 0f]).Vector, 10);
        Assert.Equal(0.5, Score(Candidate("abcdefgh"), queryEmbedding: unit, candidateEmbedding: [0f, 1f, 0f]).Vector, 10);
        Assert.Equal(0.0, Score(Candidate("abcdefgh"), queryEmbedding: unit, candidateEmbedding: [-1f, 0f, 0f]).Vector, 10);
    }

    [Fact]
    public void Total_is_the_weighted_sum_times_the_content_penalty()
    {
        var options = new MemoryScorerOptions { TemporalHalfLife = TimeSpan.FromDays(30) };
        MemoryScore score = Score(
            Candidate("alpha beta gamma", MemoryTier.Episodic, importance: 0.5, veracity: 0.8, ftsRank: -1.0),
            "alpha beta",
            [1f, 0f],
            [1f, 0f],
            options);

        double weighted =
            (score.Lexical * options.WeightLexical) +
            (score.Vector * options.WeightVector) +
            (score.Fts * options.WeightFts) +
            (score.Importance * options.WeightImportance) +
            (score.TemporalDecay * options.WeightTemporal) +
            (score.Veracity * options.WeightVeracity) +
            (score.Tier * options.WeightTier);

        Assert.Equal(weighted * score.ContentAdjustment, score.Total, 10);
        Assert.True(score.Total > 0.0);
    }

    [Fact]
    public void Total_honours_custom_weights()
    {
        var options = new MemoryScorerOptions
        {
            WeightLexical = 1.0,
            WeightVector = 0.0,
            WeightFts = 0.0,
            WeightImportance = 0.0,
            WeightTemporal = 0.0,
            WeightVeracity = 0.0,
            WeightTier = 0.0,
        };

        MemoryScore score = Score(Candidate("alpha beta gamma", MemoryTier.Working, importance: 0.0, veracity: 0.0), "alpha beta", options: options);
        Assert.Equal(1.0, score.Total, 10);
    }

    [Fact]
    public void Explain_lists_non_zero_components_in_a_stable_order()
    {
        var scorer = new MemoryScorer();
        MemoryScore score = scorer.Score(
            Candidate("alpha beta gamma", MemoryTier.Episodic, importance: 0.5, veracity: 0.8),
            "alpha beta",
            null,
            null,
            Now);

        IReadOnlyList<string> reasons = scorer.Explain(score);

        Assert.Equal(
            new[] { "lexical 1.00", "importance 0.50", "temporal 1.00", "veracity 0.80", "tier 0.70" },
            reasons);
    }

    [Fact]
    public void Explain_omits_zero_components_and_reports_the_content_penalty_last()
    {
        var scorer = new MemoryScorer();
        MemoryScore score = scorer.Score(Candidate("abc", importance: 0.0, veracity: 0.0), "zzz", null, null, Now);

        IReadOnlyList<string> reasons = scorer.Explain(score);

        Assert.Equal("content penalty x0.5", reasons[^1]);
        Assert.Equal(new[] { "temporal 1.00", "tier 1.00", "content penalty x0.5" }, reasons);
        Assert.DoesNotContain(reasons, reason => reason.StartsWith("lexical", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.StartsWith("vector", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.StartsWith("fts", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.StartsWith("importance", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.StartsWith("veracity", StringComparison.Ordinal));
    }

    [Fact]
    public void Explain_returns_nothing_for_a_fully_zero_score()
    {
        var scorer = new MemoryScorer();

        Assert.Empty(scorer.Explain(new MemoryScore(0, 0, 0, 0, 0, 0, 0, 1)));
        Assert.Empty(scorer.Explain(new MemoryScore(0, 0, 0, 0, 0, 0, 0, 0.5)));
    }

    [Fact]
    public void A_record_recalled_recently_is_not_treated_as_old_because_it_was_written_long_ago()
    {
        var wrote = Now.AddDays(-120);
        var untouched = new MemoryCandidate(
            "r", "alpha beta gamma", MemoryTier.Semantic, MemoryScope.Recall, wrote, 0.5, 1.0, 1.0);
        var usedYesterday = untouched with { LastRecalledAt = Now.AddDays(-1) };

        var cold = Score(untouched, "alpha");
        var warm = Score(usedYesterday, "alpha");

        Assert.True(warm.TemporalDecay > cold.TemporalDecay, $"warm {warm.TemporalDecay} should beat cold {cold.TemporalDecay}");
        Assert.True(warm.TemporalDecay > 0.9, "a record used yesterday is about as fresh as it gets");

        // A recall time earlier than the write (clock skew, an import) never makes a record look older.
        var skewed = untouched with { LastRecalledAt = wrote.AddDays(-10) };
        Assert.Equal(cold.TemporalDecay, Score(skewed, "alpha").TemporalDecay, 10);
    }
}

/// <summary>Token budget approximation (design doc §2.5.6 ④).</summary>
public sealed class ApproximateTokenCounterTests
{
    private readonly ApproximateTokenCounter _counter = new();

    [Fact]
    public void Counts_roughly_one_token_per_four_characters()
    {
        Assert.Equal(0, _counter.Count(""));
        Assert.Equal(1, _counter.Count("abcd"));
        Assert.Equal(1, _counter.Count("abc"));
        Assert.Equal(2, _counter.Count("abcde"));
        Assert.Equal(100, _counter.Count(new string('x', 400)));
    }

    [Fact]
    public void Count_is_never_negative_and_never_decreases_as_text_grows()
    {
        int previous = 0;
        for (int length = 0; length <= 200; length++)
        {
            int count = _counter.Count(new string('x', length));
            Assert.True(count >= previous, $"count decreased at length {length}");
            previous = count;
        }
    }

    [Fact]
    public void Chinese_text_is_priced_close_to_a_token_per_character()
    {
        var counter = new ApproximateTokenCounter();

        Assert.Equal(500, counter.Count(new string('情', 500)));
        Assert.Equal(3, counter.Count("abcdefghi")); // Latin keeps the four-characters rule
        Assert.Equal(0, counter.Count(string.Empty));
    }

    [Fact]
    public async Task A_hit_below_the_minimum_score_is_not_returned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var open = new MemoryStore(Path.Combine(dir, "all.db"), new MemoryStoreOptions());
            await using var strict = new MemoryStore(Path.Combine(dir, "strict.db"), new MemoryStoreOptions { MinScore = 0.99 });
            var record = new MemoryRecord
            {
                RecordId = "r1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Recall,
                Body = "alpha beta gamma",
            };
            await open.WriteAsync(record);
            await strict.WriteAsync(record);

            Assert.NotEmpty(await open.RecallAsync("alpha"));
            Assert.Empty(await strict.RecallAsync("alpha"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

}
