namespace RepoQL.Contracts.Embeddings;

public sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    public string Model => "disabled";
    public int Dimension => 0;
    public bool Enabled => false;
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);
    public Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<float[]?>());
}
