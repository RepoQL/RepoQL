namespace RepoQL.Contracts;

/// <summary>
/// Represents a file in the URI registry with its indexing and embedding status.
///
/// Purpose: Track the lifecycle state of a file through the indexing and embedding
/// pipelines, along with its child symbols for glob matching.
///
/// Complexity: Immutable record ensures thread-safe reads. Updates replace the entire
/// entry atomically. IReadOnlyDictionary for Symbols prevents mutation after construction.
/// </summary>
/// <param name="Status">Current indexing status.</param>
/// <param name="IndexedAt">When the file was last successfully indexed.</param>
/// <param name="Error">Error message if indexing or embedding failed.</param>
/// <param name="EmbeddingStatus">Current embedding status.</param>
/// <param name="EmbeddedChunkCount">Number of chunks from this file that have embeddings.</param>
/// <param name="EmbeddedAt">When embeddings were last successfully computed.</param>
/// <param name="Symbols">Child symbol URIs mapped to their kind (type, function, etc.).</param>
public record FileEntry(
    UriStatus Status,
    DateTime? IndexedAt,
    string? Error,
    EmbeddingStatus EmbeddingStatus,
    int EmbeddedChunkCount,
    DateTime? EmbeddedAt,
    IReadOnlyDictionary<RepoUri, string> Symbols)
{
    /// <summary>
    /// Creates a new FileEntry in Discovered state with no symbols.
    /// </summary>
    public static FileEntry Discovered() => new(
        Status: UriStatus.Discovered,
        IndexedAt: null,
        Error: null,
        EmbeddingStatus: EmbeddingStatus.Pending,
        EmbeddedChunkCount: 0,
        EmbeddedAt: null,
        Symbols: EmptySymbols);

    /// <summary>
    /// Creates a new FileEntry in Failed state with an error message.
    /// </summary>
    public static FileEntry WithError(string error) => new(
        Status: UriStatus.Failed,
        IndexedAt: null,
        Error: error,
        EmbeddingStatus: EmbeddingStatus.Pending,
        EmbeddedChunkCount: 0,
        EmbeddedAt: null,
        Symbols: EmptySymbols);

    /// <summary>
    /// Returns true if the file is fully ready for semantic search
    /// (indexed and embedded, or indexed with embedding not applicable).
    /// </summary>
    public bool IsReadyForSemanticSearch =>
        Status == UriStatus.Indexed &&
        (EmbeddingStatus == EmbeddingStatus.Embedded || EmbeddingStatus == EmbeddingStatus.NotApplicable);

    /// <summary>
    /// Returns true if the file is indexed (regardless of embedding status).
    /// </summary>
    public bool IsIndexed => Status == UriStatus.Indexed;

    private static readonly IReadOnlyDictionary<RepoUri, string> EmptySymbols =
        new Dictionary<RepoUri, string>().AsReadOnly();
}
