namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// Provider for reranking documents by relevance to a query.
/// Backed by Voyage AI rerank-2.5 via the cloud embedding service.
///
/// Purpose: Rerank explore candidates before budget allocation for higher-quality ordering.
/// Complexity: gRPC transport, score mapping, graceful degradation when unavailable.
/// </summary>
public interface IRerankProvider
{
    bool Enabled { get; }

    /// <summary>
    /// Rerank documents by relevance to a query.
    /// Returns documents in descending relevance order with scores.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="documents">Documents to rerank (index + text pairs).</param>
    /// <param name="topK">Return only top K results. 0 = return all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RerankResult> RerankAsync(
        string query,
        IReadOnlyList<RerankDocument> documents,
        int topK = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A document to rerank.
/// </summary>
/// <param name="Index">Caller-assigned index, returned unchanged in the result.</param>
/// <param name="Text">Document text to score against the query.</param>
public record RerankDocument(int Index, string Text);

/// <summary>
/// Result of a reranking operation.
/// </summary>
/// <param name="Results">Documents in descending relevance order.</param>
/// <param name="TotalTokens">Tokens consumed by the reranker.</param>
public record RerankResult(
    IReadOnlyList<RerankScore> Results,
    int TotalTokens);

/// <summary>
/// Reranking score for a single document.
/// </summary>
/// <param name="Index">Original index from the request.</param>
/// <param name="RelevanceScore">Relevance score (0.0-1.0).</param>
public record RerankScore(int Index, float RelevanceScore);

/// <summary>
/// Disabled rerank provider that always returns empty results.
/// </summary>
public sealed class DisabledRerankProvider : IRerankProvider
{
    public static readonly DisabledRerankProvider Instance = new();
    public bool Enabled => false;

    public Task<RerankResult> RerankAsync(
        string query,
        IReadOnlyList<RerankDocument> documents,
        int topK = 0,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RerankResult([], 0));
}
