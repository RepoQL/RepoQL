namespace RepoQL.Explain;

/// <summary>
/// Purpose: Synthesize answers to natural-language questions about a codebase.
/// Complexity: Orchestrates keyword extraction, broad explore search, tree context,
/// and LLM synthesis with tool use — the pure business logic of the explain tool.
/// </summary>
public interface IExplainEngine
{
    /// <summary>
    /// Answer a question by searching the codebase and synthesizing a response.
    /// </summary>
    /// <param name="question">The natural-language question to answer.</param>
    /// <param name="keywords">Optional keywords to guide search. If null, extracted from the question via LLM.</param>
    /// <param name="uriGlob">Optional URI scope to restrict the search.</param>
    /// <param name="tokenBudget">Token budget for the response.</param>
    /// <param name="cancel">Cancellation token.</param>
    Task<ExplainResult> ExecuteAsync(
        string question,
        string? keywords = null,
        string? uriGlob = null,
        int tokenBudget = 2500,
        CancellationToken cancel = default);
}

/// <summary>
/// Transport-agnostic explain result.
/// </summary>
public sealed class ExplainResult
{
    public required string Answer { get; init; }
    public IReadOnlyList<ExplainCitation>? Citations { get; init; }
    public string? Nuance { get; init; }
    public int SourceTokensConsumed { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class ExplainCitation
{
    public required string Uri { get; init; }
    public string? Snippet { get; init; }
}
