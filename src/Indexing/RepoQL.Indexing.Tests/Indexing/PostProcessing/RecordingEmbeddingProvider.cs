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

    public Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        if (texts is null || texts.Count == 0)
            return Task.FromResult(Array.Empty<float[]?>());

        var batch = new float[]?[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            EmbedCount++;
            batch[i] = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        }

        return Task.FromResult(batch);
    }
}
