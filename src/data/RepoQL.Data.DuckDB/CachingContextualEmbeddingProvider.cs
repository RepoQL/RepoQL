using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Reuse contextual embeddings by checking the local parquet cache before calling the remote provider.
/// Complexity: Group-level content hashing (context + all chunks determine each chunk's vector),
/// all-or-nothing hit/miss per group, miss-only delegation, and cache write-back.
/// </summary>
public sealed class CachingContextualEmbeddingProvider : IContextualEmbeddingProvider
{
    private readonly IContextualEmbeddingProvider _inner;
    private readonly EmbeddingCache _cache;
    private readonly ILogger _logger;

    public CachingContextualEmbeddingProvider(
        IContextualEmbeddingProvider inner,
        EmbeddingCache cache,
        ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Model => _inner.Model;
    public int Dimension => _inner.Dimension;
    public bool Enabled => _inner.Enabled;

    public void SetUseCaseHint(string useCase) => _inner.SetUseCaseHint(useCase);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _inner.InitializeAsync(cancellationToken);

    public async Task<ContextualEmbeddingResult> EmbedChunksAsync(
        IReadOnlyList<DocumentChunkGroup> groups,
        CancellationToken cancellationToken = default)
    {
        if (groups.Count == 0)
            return new ContextualEmbeddingResult([], 0);

        if (!_inner.Enabled || !_cache.Enabled)
            return await _inner.EmbedChunksAsync(groups, cancellationToken).ConfigureAwait(false);

        // 1. Compute per-chunk cache keys (grouped by group content hash).
        var groupKeys = new string[groups.Count][];    // [groupIdx][chunkIdx] = cache key
        var groupHashes = new string[groups.Count];
        var allKeys = new List<string>();

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var groupHash = ComputeGroupContentHash(group.Context, group.Chunks);
            groupHashes[g] = groupHash;
            groupKeys[g] = new string[group.Chunks.Count];

            for (var c = 0; c < group.Chunks.Count; c++)
            {
                var key = CachingEmbeddingProvider.ComputeContentHash(
                    Model, "c", string.Concat(groupHash, "\0", c.ToString()));
                groupKeys[g][c] = key;
                allKeys.Add(key);
            }
        }

        // 2. Bulk lookup.
        var cacheHits = await _cache.LookupAsync(
            allKeys.ToArray(), Model, cancellationToken).ConfigureAwait(false);

        // 3. Partition groups into hits (all chunks cached) vs misses.
        var hitGroups = new List<int>();
        var missGroups = new List<int>();

        for (var g = 0; g < groups.Count; g++)
        {
            var allCached = true;
            for (var c = 0; c < groupKeys[g].Length; c++)
            {
                if (!cacheHits.TryGetValue(groupKeys[g][c], out var cached) ||
                    !IsUsable(cached))
                {
                    allCached = false;
                    break;
                }
            }

            if (allCached && groupKeys[g].Length > 0)
                hitGroups.Add(g);
            else
                missGroups.Add(g);
        }

        LogHitRate(hitGroups.Count, groups.Count, missGroups.Count);

        // 4. Collect cached vectors for hit groups.
        var results = new List<ContextualChunkVector>();

        foreach (var g in hitGroups)
        {
            for (var c = 0; c < groupKeys[g].Length; c++)
            {
                var cached = cacheHits[groupKeys[g][c]];
                results.Add(new ContextualChunkVector(g, c, cached.Embedding, null));
            }
        }

        if (missGroups.Count == 0)
            return new ContextualEmbeddingResult(results, 0);

        // 5. Call inner for miss groups only.
        var missInputs = new List<DocumentChunkGroup>(missGroups.Count);
        var missOriginalIndices = new List<int>(missGroups.Count);

        for (var i = 0; i < missGroups.Count; i++)
        {
            var g = missGroups[i];
            missInputs.Add(groups[g]);
            missOriginalIndices.Add(g);
        }

        var innerResult = await _inner.EmbedChunksAsync(missInputs, cancellationToken).ConfigureAwait(false);

        // 6. Write back and collect results, remapping group indices.
        var writeBack = new List<CacheEntry>();

        foreach (var vec in innerResult.Vectors)
        {
            var originalGroupIdx = missOriginalIndices[vec.GroupIndex];
            results.Add(new ContextualChunkVector(originalGroupIdx, vec.ChunkIndex, vec.Vector, vec.Error));

            if (vec.Vector is { Length: > 0 })
            {
                writeBack.Add(new CacheEntry(
                    TextHash: groupKeys[originalGroupIdx][vec.ChunkIndex],
                    Model: Model,
                    MaxDim: vec.Vector.Length,
                    Embedding: vec.Vector,
                    CreatedAt: DateTimeOffset.UtcNow));
            }
        }

        if (writeBack.Count > 0)
            await _cache.WriteBackAsync(writeBack, cancellationToken).ConfigureAwait(false);

        return new ContextualEmbeddingResult(results, innerResult.TotalTokens);
    }

    public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
        => _inner.EmbedQueryAsync(text, cancellationToken);

    internal static string ComputeGroupContentHash(string? context, IReadOnlyList<string> chunks)
    {
        var sb = new StringBuilder();
        sb.Append(context ?? "");
        sb.Append('\0');
        sb.Append(chunks.Count);
        foreach (var chunk in chunks)
        {
            sb.Append('\0');
            sb.Append(chunk);
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private bool IsUsable(CachedEmbedding cached)
        => cached.Embedding is { Length: > 0 } && cached.MaxDim == Dimension && cached.Embedding.Length == Dimension;

    private void LogHitRate(int hits, int total, int misses)
    {
        var percent = total == 0 ? 0.0 : (hits * 100.0) / total;
        _logger.LogInformation(
            "Contextual embedding cache: {Hits}/{Total} groups cached ({Percent:F1}%), {Misses} groups to embed",
            hits, total, percent, misses);
    }
}
