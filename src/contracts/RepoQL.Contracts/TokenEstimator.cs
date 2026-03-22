using LLMSharp.Anthropic.Tokenizer;

namespace RepoQL.Contracts;

/// <summary>
/// Estimates token counts for text using the Claude BPE tokenizer.
///
/// Purpose: Provides accurate token counts for text content, used during indexing to populate
/// the token_count field on artifacts and for budget-based output rendering decisions.
///
/// Complexity: Wraps the ClaudeTokenizer which loads BPE rank maps from embedded resources on first use.
/// The rest of the system is protected from this complexity via a simple static interface.
/// </summary>
public static class TokenEstimator
{
    private static readonly Lazy<ClaudeTokenizer> Tokenizer = new(() => new ClaudeTokenizer());

    /// <summary>
    /// Count tokens for a string using the Claude BPE tokenizer.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <returns>The number of tokens, or 0 for null/empty text.</returns>
    public static int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Tokenizer.Value.CountTokens(text);

    /// <summary>
    /// Safely estimate tokens, returning null if estimation fails or text is null/empty.
    /// Useful for artifact indexing where null indicates "not computed" vs 0.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <returns>The number of tokens, or null for empty/failed estimation.</returns>
    public static int? EstimateTokensSafe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        try
        {
            var count = Tokenizer.Value.CountTokens(text);
            return count > 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }
}
