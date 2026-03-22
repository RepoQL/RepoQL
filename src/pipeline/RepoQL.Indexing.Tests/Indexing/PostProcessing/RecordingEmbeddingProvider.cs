using RepoQL.Contracts.Embeddings;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal sealed class RecordingEmbeddingProvider : IEmbeddingProvider
{
    public int EmbedCount { get; private set; }
    public List<int> EmbeddedTextLengths { get; } = new();
    public string Model => "test";
    public int Dimension => 4;
    public bool Enabled => true;

    public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        EmbedCount++;
        EmbeddedTextLengths.Add(text.Length);
        return Task.FromResult<float[]?>(new[] { 0.1f, 0.2f, 0.3f, 0.4f });
    }

    public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
    {
        EmbedCount++;
        EmbeddedTextLengths.Add(text.Length);
        return Task.FromResult<float[]?>(new[] { 0.1f, 0.2f, 0.3f, 0.4f });
    }

    public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        return EmbedBatchCore(texts);
    }

    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        return EmbedBatchCore(texts);
    }

    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
    {
        return EmbedBatchCore(texts);
    }

    private Task<float[]?[]> EmbedBatchCore(IReadOnlyList<string>? texts)
    {
        if (texts is null || texts.Count == 0)
            return Task.FromResult(Array.Empty<float[]?>());

        var batch = new float[]?[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            EmbedCount++;
            EmbeddedTextLengths.Add(texts[i]?.Length ?? 0);
            batch[i] = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        }

        return Task.FromResult(batch);
    }
}
