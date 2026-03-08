namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// Provider for contextual embeddings where chunks are grouped by document.
/// Chunks within a group are embedded with mutual awareness — the embedding of each chunk
/// is influenced by its siblings. This produces significantly better retrieval quality
/// compared to embedding chunks independently.
/// </summary>
public interface IContextualEmbeddingProvider
{
    string Model { get; }
    int Dimension { get; }
    bool Enabled { get; }

    /// <summary>
    /// Ensure model metadata (Model, Dimension) is populated.
    /// Called once before the first embedding request so the refresh plan
    /// can determine the active model before any vectors are generated.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Embed grouped chunks with document-level context.
    /// Each group represents one document's chunks in document order.
    /// The provider sends them to the embedding model as a unit so chunks
    /// benefit from mutual contextual awareness.
    /// </summary>
    Task<ContextualEmbeddingResult> EmbedChunksAsync(
        IReadOnlyList<DocumentChunkGroup> groups,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed a single query string for search.
    /// Uses the same model and vector space as <see cref="EmbedChunksAsync"/>.
    /// </summary>
    Task<float[]?> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A document's chunks for contextual embedding.
/// </summary>
/// <param name="DocumentUri">Document URI for logging/metering.</param>
/// <param name="Context">Document-level context (x-ray headline, summary, structure).
/// Sent to the model but does NOT receive a vector — only chunks get vectors.</param>
/// <param name="Chunks">Ordered content chunks from this document.</param>
public record DocumentChunkGroup(string DocumentUri, string? Context, IReadOnlyList<string> Chunks);

/// <summary>
/// Result of a contextual embedding operation.
/// </summary>
/// <param name="Vectors">One result per input chunk. Check <see cref="ContextualChunkVector.Error"/> for partial failures.</param>
/// <param name="TotalTokens">Tokens consumed by the embedding provider.</param>
public record ContextualEmbeddingResult(
    IReadOnlyList<ContextualChunkVector> Vectors,
    int TotalTokens);

/// <summary>
/// Embedding result for a single chunk within a contextual group.
/// </summary>
/// <param name="GroupIndex">Index of the group in the original request.</param>
/// <param name="ChunkIndex">Index of the chunk within its group.</param>
/// <param name="Vector">The embedding vector, or null if this chunk failed.</param>
/// <param name="Error">Non-null if this chunk failed (e.g. exceeded token limit).</param>
public record ContextualChunkVector(int GroupIndex, int ChunkIndex, float[]? Vector, string? Error);
