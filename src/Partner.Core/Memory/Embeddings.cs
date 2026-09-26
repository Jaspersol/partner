using System.Text;

namespace Partner.Core.Memory;

/// <summary>Embedding backend for the memory layer.</summary>
public interface IEmbeddingProvider
{
    string ModelId { get; }

    int Dimensions { get; }

    /// <summary>Returns null when the provider cannot embed (offline / disabled) — callers must degrade, not fail.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>Offline default: no embeddings at all (the store then runs FTS-only).</summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public string ModelId => "none";

    public int Dimensions => 0;

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
}

/// <summary>
/// Deterministic, dependency-free local embedding: hashed character n-grams folded into a fixed-size
/// unit vector. Weak but real (shared substrings raise cosine similarity) and stable across runs.
/// </summary>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    private readonly int _ngramSize;

    public HashingEmbeddingProvider(int dimensions = 384, int ngramSize = 3)
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be positive.");
        }

        if (ngramSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ngramSize), ngramSize, "N-gram size must be positive.");
        }

        Dimensions = dimensions;
        _ngramSize = ngramSize;
    }

    public string ModelId => "hashing-ngram-v1";

    public int Dimensions { get; }

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(Embed(text));

    /// <summary>Always returns a vector of <see cref="Dimensions"/> floats, never null; all zeros for unusable text.</summary>
    internal float[] Embed(string? text)
    {
        var vector = new float[Dimensions];
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return vector;
        }

        // Shorter-than-n-gram text is hashed as a single gram rather than dropped.
        int gramSize = Math.Min(_ngramSize, normalized.Length);
        int gramCount = normalized.Length - gramSize + 1;
        for (int start = 0; start < gramCount; start++)
        {
            uint hash = Fnv1a(normalized, start, gramSize);
            int index = (int)(hash % (uint)Dimensions);
            vector[index] += (hash & 0x80000000u) == 0 ? 1f : -1f;
        }

        return L2Normalize(vector);
    }

    /// <summary>Lower-cases and keeps only letters/digits so punctuation and spacing do not fragment grams.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static uint Fnv1a(string text, int start, int length)
    {
        uint hash = FnvOffsetBasis;
        for (int i = start; i < start + length; i++)
        {
            char c = text[i];
            hash ^= (byte)(c & 0xFF);
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        return hash;
    }

    private static float[] L2Normalize(float[] vector)
    {
        double sumOfSquares = 0.0;
        foreach (float value in vector)
        {
            sumOfSquares += (double)value * value;
        }

        if (sumOfSquares <= 0.0)
        {
            return vector;
        }

        float norm = (float)Math.Sqrt(sumOfSquares);
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }
}

/// <summary>Cosine similarity with explicit degradation to zero for unusable input.</summary>
public static class CosineSimilarity
{
    /// <summary>0 when either vector is null/empty/mismatched/dimension-zero; otherwise in [-1,1].</summary>
    public static double Compute(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || b.Length == 0 || a.Length != b.Length)
        {
            return 0.0;
        }

        double dot = 0.0;
        double normA = 0.0;
        double normB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        if (normA <= 0.0 || normB <= 0.0)
        {
            return 0.0;
        }

        double similarity = dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        return double.IsNaN(similarity) ? 0.0 : Math.Clamp(similarity, -1.0, 1.0);
    }
}
