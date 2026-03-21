namespace RepoQL.Explore.Search;

/// <summary>
/// Service for searching objects (symbols) within documents.
/// </summary>
public interface IObjectSearchService
{
    /// <summary>
    /// Search for objects within the specified documents.
    /// </summary>
    /// <param name="documentUris">URIs of documents to search within.</param>
    /// <param name="question">Semantic search query for re-ranking (optional).</param>
    /// <param name="objectsPerDocument">Maximum objects per document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching objects grouped by document.</returns>
    Task<IReadOnlyList<ObjectMatch>> SearchInDocumentsAsync(
        IReadOnlyList<string> documentUris,
        string? question,
        int objectsPerDocument,
        CancellationToken cancellationToken);
}
