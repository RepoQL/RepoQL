namespace RepoQL.Contracts.Embeddings;

public sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    public string Model => "disabled";
    public int Dimension => 0;
    public bool Enabled => false;
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);
}