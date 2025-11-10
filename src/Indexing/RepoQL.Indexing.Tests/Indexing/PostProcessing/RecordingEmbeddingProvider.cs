using RepoQL.Contracts.Embeddings;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal sealed class RecordingEmbeddingProvider : IEmbeddingProvider
{
    public int EmbedCount { get; private set; }
    public string Model => "test";
    public int Dimension => 4;
    public bool Enabled => true;

    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        EmbedCount++;
        return Task.FromResult<float[]?>(new[] { 0.1f, 0.2f, 0.3f, 0.4f });
    }
}