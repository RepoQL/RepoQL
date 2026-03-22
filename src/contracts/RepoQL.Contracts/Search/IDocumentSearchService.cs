namespace RepoQL.Contracts.Search;

/// <summary>
/// Result from document search including chunk scores for proximity boosting.
/// </summary>
public record DocumentSearchResult(
    IReadOnlyList<DocumentMatch> Documents,
    IReadOnlyDictionary<string, IReadOnlyList<ChunkScore>> ChunkScores
);

/// <summary>
/// Service for searching documents (files).
/// </summary>
public interface IDocumentSearchService
{
    /// <summary>
    /// Search for documents matching the criteria.
    /// </summary>
    /// <param name="scope">Glob pattern to filter by path.</param>
    /// <param name="question">Semantic search query (optional).</param>
    /// <param name="limit">Maximum documents to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching documents with chunk scores for proximity boosting.</returns>
    Task<DocumentSearchResult> SearchAsync(
        string? scope,
        string? question,
        int limit,
        CancellationToken cancellationToken);
}
