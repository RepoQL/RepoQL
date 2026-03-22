namespace RepoQL.Contracts.Search;

/// <summary>
/// Configuration for JIT embedding enrichment within explore search.
/// </summary>
public sealed class ObjectSearchConfig
{
    /// <summary>Maximum JIT embeddings to compute per search.</summary>
    public int MaxJitEmbeddings { get; init; } = 30;

    /// <summary>Expected value threshold for JIT embedding selection.</summary>
    public double JitEmbeddingThreshold { get; init; } = 0.15;
}
