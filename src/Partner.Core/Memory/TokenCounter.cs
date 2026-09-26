namespace Partner.Core.Memory;

/// <summary>Counts tokens for budgeting; implementations are dependency-free and deterministic.</summary>
public interface ITokenCounter
{
    int Count(string text);
}

/// <summary>Dependency-free approximation: ~1 token per 4 characters, never zero for non-empty text.</summary>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    private const int CharsPerToken = 4;

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // Round up so any non-empty text costs at least one token.
        return (text.Length + CharsPerToken - 1) / CharsPerToken;
    }
}
