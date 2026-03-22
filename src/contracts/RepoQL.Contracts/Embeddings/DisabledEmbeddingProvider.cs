namespace RepoQL.Contracts.Embeddings;

public sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    public string Model => "disabled";
    public int Dimension => 0;
    public bool Enabled => false;

    public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);

    public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);

    public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<float[]?>());

    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<float[]?>());

    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<float[]?>());
}
