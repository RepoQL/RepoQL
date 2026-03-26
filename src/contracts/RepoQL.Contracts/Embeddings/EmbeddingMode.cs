namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// Controls the level of embedding generation for resource-constrained environments.
/// </summary>
public enum EmbeddingMode
{
    /// <summary>
    /// No embeddings generated. Semantic search will be unavailable.
    /// Use when: Very limited hardware, or embeddings not needed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Only structure embeddings (headlines, file structure) are generated.
    /// Enables search() for document-level semantic search with structure scoring.
    /// Use when: Limited hardware but want basic semantic search.
    /// </summary>
    StructureOnly = 1,

    /// <summary>
    /// Full embeddings including content chunks.
    /// Enables search() with full semantic capabilities including chunk-level scoring.
    /// Use when: Good hardware, full semantic search needed.
    /// </summary>
    Full = 2,

    /// <summary>
    /// Hybrid mode: structure-only for files with parsers, full for files without.
    /// Uses structure embedding when a parser has extracted meaningful structure,
    /// falls back to full embedding for plain text files without semantic extraction.
    /// Use when: Want efficient embeddings that leverage parser output.
    /// </summary>
    Hybrid = 3
}

/// <summary>
/// Extension methods for <see cref="EmbeddingMode"/>.
/// </summary>
public static class EmbeddingModeExtensions
{
    /// <summary>
    /// Returns true if structure embeddings should be generated.
    /// </summary>
    public static bool IncludesStructure(this EmbeddingMode mode) => mode >= EmbeddingMode.StructureOnly;

    /// <summary>
    /// Returns true if full content embeddings should be generated.
    /// </summary>
    public static bool IncludesFull(this EmbeddingMode mode) => mode == EmbeddingMode.Full;

    /// <summary>
    /// Returns true if this is hybrid mode (structure when available, full otherwise).
    /// </summary>
    public static bool IsHybrid(this EmbeddingMode mode) => mode == EmbeddingMode.Hybrid;

}
