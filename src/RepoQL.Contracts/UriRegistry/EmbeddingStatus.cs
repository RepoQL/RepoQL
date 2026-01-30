namespace RepoQL.Contracts;

/// <summary>
/// Lifecycle status of a file's embeddings for semantic search.
/// </summary>
public enum EmbeddingStatus
{
    /// <summary>File is indexed but embeddings have not yet been computed.</summary>
    Pending,

    /// <summary>Embeddings are currently being computed.</summary>
    Embedding,

    /// <summary>Embeddings have been successfully computed.</summary>
    Embedded,

    /// <summary>Embedding computation failed for this file.</summary>
    Failed,

    /// <summary>File type does not support embeddings (e.g., binary files).</summary>
    NotApplicable
}
