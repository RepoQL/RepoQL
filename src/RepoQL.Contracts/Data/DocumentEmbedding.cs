namespace RepoQL.Contracts.Data;

/// <summary>
/// An embedding for a document or object node, used for semantic search.
/// </summary>
/// <param name="DocumentId">The document node ID.</param>
/// <param name="NodeId">The node ID (same as DocumentId for document-level, different for object-level).</param>
/// <param name="ChunkIndex">Chunk index (0 for whole content or first chunk, 1+ for subsequent chunks).</param>
/// <param name="EmbeddingType">Type discriminator: 'structure' (x-ray based) or 'full' (content based).</param>
/// <param name="Uri">The document or node URI.</param>
/// <param name="Scope">Embedding scope: 'document' or 'object'.</param>
/// <param name="Vector">The embedding vector.</param>
/// <param name="Model">The embedding model name.</param>
/// <param name="Dimension">The embedding dimension.</param>
/// <param name="StartByte">Start byte offset for chunked content (null for whole content).</param>
/// <param name="EndByte">End byte offset for chunked content (null for whole content).</param>
public sealed record DocumentEmbedding(
    Guid DocumentId,
    Guid NodeId,
    int ChunkIndex,
    string EmbeddingType,
    string Uri,
    string Scope,
    float[] Vector,
    string Model,
    int Dimension,
    long? StartByte = null,
    long? EndByte = null)
{
    /// <summary>Structure embedding type - based on headline/summary/structure.</summary>
    public const string TypeStructure = "structure";

    /// <summary>Full embedding type - based on actual content.</summary>
    public const string TypeFull = "full";

    /// <summary>Document-level scope.</summary>
    public const string ScopeDocument = "document";

    /// <summary>Object-level scope (symbols, functions, etc).</summary>
    public const string ScopeObject = "object";
}
