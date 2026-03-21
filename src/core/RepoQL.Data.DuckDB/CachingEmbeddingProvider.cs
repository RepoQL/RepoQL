using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Reuse deterministic embeddings by checking the local parquet cache before provider inference.
/// Complexity: Hash key generation, batch hit/miss partitioning, miss-only delegation, progress remapping,
/// and non-blocking cache write-back while preserving IEmbeddingProvider behavior.
/// </summary>
public sealed class CachingEmbeddingProvider : IEmbeddingProvider
{
    private readonly IEmbeddingProvider _inner;
    private readonly EmbeddingCache _cache;
    private readonly ILogger<CachingEmbeddingProvider> _logger;

    public CachingEmbeddingProvider(
        IEmbeddingProvider inner,
        EmbeddingCache cache,
        ILogger<CachingEmbeddingProvider>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger<CachingEmbeddingProvider>.Instance;
    }

    public string Model => _inner.Model;

    public int Dimension => _inner.Dimension;

    public bool Enabled => _inner.Enabled;

    public async Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchCoreAsync(
                [text],
                type: "q",
                callBatch: (texts, ct) => _inner.EmbedQueryBatchAsync(texts, ct),
                callBatchWithProgress: null,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        return results.Length > 0 ? results[0] : null;
    }

    public async Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchCoreAsync(
                [text],
                type: "p",
                callBatch: (texts, ct) => _inner.EmbedPassageBatchAsync(texts, ct),
                callBatchWithProgress: null,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        return results.Length > 0 ? results[0] : null;
    }

    public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(
            texts,
            type: "q",
            callBatch: (misses, ct) => _inner.EmbedQueryBatchAsync(misses, ct),
            callBatchWithProgress: null,
            progress: null,
            cancellationToken);

    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(
            texts,
            type: "p",
            callBatch: (misses, ct) => _inner.EmbedPassageBatchAsync(misses, ct),
            callBatchWithProgress: null,
            progress: null,
            cancellationToken);

    public Task<float[]?[]> EmbedPassageBatchAsync(
        IReadOnlyList<string>? texts,
        BatchEmbeddingProgress progress,
        CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(
            texts,
            type: "p",
            callBatch: (misses, ct) => _inner.EmbedPassageBatchAsync(misses, ct),
            callBatchWithProgress: (misses, adjustedProgress, ct) =>
                _inner.EmbedPassageBatchAsync(misses, adjustedProgress, ct),
            progress: progress,
            cancellationToken);

    public static string ComputeContentHash(string model, string type, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(text);

        var bytes = Encoding.UTF8.GetBytes(string.Concat(model, "\0", type, "\0", text));
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task<float[]?[]> EmbedBatchCoreAsync(
        IReadOnlyList<string>? texts,
        string type,
        Func<IReadOnlyList<string>, CancellationToken, Task<float[]?[]>> callBatch,
        Func<IReadOnlyList<string>, BatchEmbeddingProgress, CancellationToken, Task<float[]?[]>>? callBatchWithProgress,
        BatchEmbeddingProgress? progress,
        CancellationToken cancellationToken)
    {
        if (texts is null || texts.Count == 0)
            return [];

        if (!_inner.Enabled || !_cache.Enabled)
        {
            return progress is { } p && callBatchWithProgress is not null
                ? await callBatchWithProgress(texts, p, cancellationToken).ConfigureAwait(false)
                : await callBatch(texts, cancellationToken).ConfigureAwait(false);
        }

        var hashes = new string[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes[i] = ComputeContentHash(Model, type, texts[i]);
        }

        var cacheHits = await _cache.LookupAsync(hashes, Model, cancellationToken).ConfigureAwait(false);

        var results = new float[]?[texts.Count];
        var missIndices = new List<int>(texts.Count);
        var missTexts = new List<string>(texts.Count);
        var missHashes = new List<string>(texts.Count);
        var hitCount = 0;

        for (var i = 0; i < texts.Count; i++)
        {
            var hash = hashes[i];
            if (cacheHits.TryGetValue(hash, out var cached) && TryUseCachedEmbedding(cached, out var cachedEmbedding))
            {
                results[i] = cachedEmbedding;
                hitCount++;
                continue;
            }

            missIndices.Add(i);
            missTexts.Add(texts[i]);
            missHashes.Add(hash);
        }

        LogHitRate(hitCount, texts.Count, missTexts.Count);

        if (missTexts.Count == 0)
            return results;

        var computed = progress is { } batchProgress && callBatchWithProgress is not null
            ? await callBatchWithProgress(
                missTexts,
                CreateMissProgress(batchProgress, missTexts.Count),
                cancellationToken).ConfigureAwait(false)
            : await callBatch(missTexts, cancellationToken).ConfigureAwait(false);

        var writeBack = new List<CacheEntry>(missTexts.Count);
        for (var missIndex = 0; missIndex < missIndices.Count; missIndex++)
        {
            var originalIndex = missIndices[missIndex];
            var embedding = missIndex < computed.Length ? computed[missIndex] : null;
            results[originalIndex] = embedding;

            if (embedding is null)
                continue;

            writeBack.Add(new CacheEntry(
                TextHash: missHashes[missIndex],
                Model: Model,
                MaxDim: embedding.Length,
                Embedding: embedding,
                CreatedAt: DateTimeOffset.UtcNow));
        }

        await _cache.WriteBackAsync(writeBack, cancellationToken).ConfigureAwait(false);
        return results;
    }

    private bool TryUseCachedEmbedding(CachedEmbedding cached, out float[] embedding)
    {
        if (cached.Embedding is { Length: > 0 } vector && cached.MaxDim == Dimension && vector.Length == Dimension)
        {
            embedding = vector;
            return true;
        }

        embedding = [];
        return false;
    }

    private static BatchEmbeddingProgress CreateMissProgress(BatchEmbeddingProgress original, int misses)
    {
        return new BatchEmbeddingProgress(
            original.BatchNumber,
            original.TotalBatches,
            misses,
            misses,
            original.ElapsedTime);
    }

    private void LogHitRate(int hits, int total, int misses)
    {
        var percent = total == 0 ? 0.0 : (hits * 100.0) / total;
        _logger.LogInformation(
            "Embedding cache: {Hits}/{Total} hits ({Percent:F1}%), {Misses} to compute",
            hits,
            total,
            percent,
            misses);
    }
}
