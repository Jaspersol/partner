using Partner.Core.Memory;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// Embedding backends (design doc §2.5.6 ⑤): the offline default degrades to FTS-only, the hashing
/// provider is deterministic and genuinely reflects shared substrings.
/// </summary>
public sealed class EmbeddingsTests
{
    [Fact]
    public async Task Null_provider_degrades_to_no_embeddings()
    {
        var provider = new NullEmbeddingProvider();

        Assert.Equal("none", provider.ModelId);
        Assert.Equal(0, provider.Dimensions);
        Assert.Null(await provider.EmbedAsync("anything at all"));
        Assert.Null(await provider.EmbedAsync(string.Empty));
    }

    [Fact]
    public void Hashing_provider_exposes_its_configuration()
    {
        Assert.Equal("hashing-ngram-v1", new HashingEmbeddingProvider().ModelId);
        Assert.Equal(384, new HashingEmbeddingProvider().Dimensions);
        Assert.Equal(64, new HashingEmbeddingProvider(dimensions: 64).Dimensions);
    }

    [Fact]
    public async Task Hashing_provider_is_deterministic_and_unit_normalised()
    {
        var provider = new HashingEmbeddingProvider();

        float[] first = await Embed(provider, "the quick brown fox jumps over the lazy dog");
        float[] second = await Embed(provider, "the quick brown fox jumps over the lazy dog");

        Assert.Equal(provider.Dimensions, first.Length);
        Assert.True(first.AsSpan().SequenceEqual(second), "identical input must produce a bitwise-equal vector");
        Assert.Equal(1.0, Norm(first), 3);
    }

    [Fact]
    public async Task Hashing_provider_returns_all_zeros_when_there_are_no_usable_characters()
    {
        var provider = new HashingEmbeddingProvider();

        foreach (string text in new[] { "", "   ", "!!!", "?,.;: ", "\t\n" })
        {
            float[] vector = await Embed(provider, text);
            Assert.Equal(provider.Dimensions, vector.Length);
            Assert.All(vector, value => Assert.Equal(0f, value));
        }
    }

    [Fact]
    public async Task Hashing_provider_produces_different_vectors_for_different_text()
    {
        var provider = new HashingEmbeddingProvider();

        float[] first = await Embed(provider, "alpha beta gamma");
        float[] second = await Embed(provider, "zebra quantum flux");

        Assert.False(first.AsSpan().SequenceEqual(second));
        Assert.True(CosineSimilarity.Compute(first, second) < 0.9);
    }

    [Fact]
    public async Task Hashing_provider_scores_shared_substrings_above_unrelated_text()
    {
        var provider = new HashingEmbeddingProvider();

        float[] query = await Embed(provider, "the quick brown fox");
        float[] related = await Embed(provider, "the quick brown fox jumps over the lazy dog");
        float[] unrelated = await Embed(provider, "zebra quantum flux capacitor");

        double relatedSimilarity = CosineSimilarity.Compute(query, related);
        double unrelatedSimilarity = CosineSimilarity.Compute(query, unrelated);

        Assert.True(relatedSimilarity > unrelatedSimilarity, $"related {relatedSimilarity} <= unrelated {unrelatedSimilarity}");
        Assert.True(relatedSimilarity > 0.5);
    }

    [Fact]
    public async Task Hashing_provider_handles_text_shorter_than_the_ngram_size()
    {
        var provider = new HashingEmbeddingProvider(dimensions: 16, ngramSize: 5);

        float[] vector = await Embed(provider, "ab");

        Assert.Equal(16, vector.Length);
        Assert.Equal(1.0, Norm(vector), 3);
    }

    [Fact]
    public void Cosine_similarity_degrades_to_zero_for_unusable_input()
    {
        Assert.Equal(0.0, CosineSimilarity.Compute(null, [1f, 0f]));
        Assert.Equal(0.0, CosineSimilarity.Compute([1f, 0f], null));
        Assert.Equal(0.0, CosineSimilarity.Compute(null, null));
        Assert.Equal(0.0, CosineSimilarity.Compute([], [1f, 0f]));
        Assert.Equal(0.0, CosineSimilarity.Compute([1f, 0f], []));
        Assert.Equal(0.0, CosineSimilarity.Compute([1f, 0f, 0f], [1f, 0f]));
        Assert.Equal(0.0, CosineSimilarity.Compute([0f, 0f], [1f, 0f]));
        Assert.Equal(0.0, CosineSimilarity.Compute([0f, 0f], [0f, 0f]));
    }

    [Fact]
    public void Cosine_similarity_of_identical_and_opposite_vectors_is_one_and_minus_one()
    {
        Assert.Equal(1.0, CosineSimilarity.Compute([1f, 0f], [1f, 0f]), 10);
        Assert.Equal(1.0, CosineSimilarity.Compute([0.5f, 0.5f], [0.25f, 0.25f]), 10);
        Assert.Equal(-1.0, CosineSimilarity.Compute([1f, 0f], [-1f, 0f]), 10);
        Assert.Equal(0.0, CosineSimilarity.Compute([1f, 0f], [0f, 1f]), 10);
    }

    private static async Task<float[]> Embed(IEmbeddingProvider provider, string text)
    {
        float[]? vector = await provider.EmbedAsync(text);
        Assert.NotNull(vector);
        return vector;
    }

    private static double Norm(float[] vector)
    {
        double sumOfSquares = 0.0;
        foreach (float value in vector)
        {
            sumOfSquares += (double)value * value;
        }

        return Math.Sqrt(sumOfSquares);
    }
}
