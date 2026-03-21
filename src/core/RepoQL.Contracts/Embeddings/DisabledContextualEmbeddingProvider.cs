namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// No-op contextual embedding provider returned when remote embedding is not configured.
/// </summary>
public sealed class DisabledContextualEmbeddingProvider : IContextualEmbeddingProvider
{
    public string Model => "disabled";
    public int Dimension => 0;
    public bool Enabled => false;

    public Task<ContextualEmbeddingResult> EmbedChunksAsync(
        IReadOnlyList<DocumentChunkGroup> groups,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ContextualEmbeddingResult([], 0));

    public Task<float[]?> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);
}
