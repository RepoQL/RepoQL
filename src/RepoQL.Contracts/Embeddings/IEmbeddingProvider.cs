namespace RepoQL.Contracts.Embeddings;

public interface IEmbeddingProvider
{
    string Model { get; }
    int Dimension { get; }
    bool Enabled { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    public string Model => "disabled";
    public int Dimension => 0;
    public bool Enabled => false;
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);
}

