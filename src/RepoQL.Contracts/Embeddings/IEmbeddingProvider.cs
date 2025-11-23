namespace RepoQL.Contracts.Embeddings;

public interface IEmbeddingProvider
{
    string Model { get; }
    int Dimension { get; }
    bool Enabled { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default);
}
