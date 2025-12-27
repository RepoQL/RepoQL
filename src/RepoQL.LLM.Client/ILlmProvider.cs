namespace RepoQL.LLM.Client;

/// <summary>
/// Interface for LLM-powered operations on repository data.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Whether the LLM provider is configured and enabled.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// The model being used (for logging/diagnostics).
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Summarize data with respect to caller's intent.
    /// </summary>
    /// <param name="jsonData">JSON array of result rows from the query.</param>
    /// <param name="intent">What the caller hoped to find/understand.</param>
    /// <param name="maxTokens">Approximate token limit for the summary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Text summary addressing the caller's intent.</returns>
    Task<string> SummarizeAsync(
        string jsonData,
        string intent,
        int maxTokens = 500,
        CancellationToken ct = default);

    /// <summary>
    /// Extract relevant snippets with tool access to read URIs.
    /// Returns markdown report with code blocks and synthesis.
    /// </summary>
    /// <param name="jsonData">JSON array of result rows from the query.</param>
    /// <param name="intent">What the caller is looking for.</param>
    /// <param name="readUri">Tool callback: (uri, contextLines) => content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Markdown report with URIs, code snippets, and synthesis.</returns>
    Task<string> ExtractAsync(
        string jsonData,
        string intent,
        Func<string, int, string> readUri,
        CancellationToken ct = default);
}
