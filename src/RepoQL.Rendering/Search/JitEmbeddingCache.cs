using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RepoQL.Rendering.Search;

/// <summary>
/// Session-level cache for JIT embeddings computed during object search.
/// Not persisted to database - lives only for duration of search session.
/// Thread-safe for concurrent access within a session.
/// </summary>
public sealed class JitEmbeddingCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private long _accessCounter;

    /// <summary>
    /// Creates a new JIT embedding cache.
    /// </summary>
    /// <param name="maxEntries">Maximum entries before eviction (default 500).</param>
    public JitEmbeddingCache(int maxEntries = 500)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Try to get a cached embedding by content hash.
    /// </summary>
    /// <param name="contentHash">SHA256 hash of the text content.</param>
    /// <returns>The cached embedding or null if not found.</returns>
    public float[]? TryGet(string contentHash)
    {
        if (_cache.TryGetValue(contentHash, out var entry))
        {
            entry.LastAccess = Interlocked.Increment(ref _accessCounter);
            return entry.Embedding;
        }
        return null;
    }

    /// <summary>
    /// Add an embedding to the cache.
    /// Evicts least recently used entries if at capacity.
    /// </summary>
    /// <param name="contentHash">SHA256 hash of the text content.</param>
    /// <param name="embedding">The embedding vector to cache.</param>
    public void Add(string contentHash, float[] embedding)
    {
        // Evict if at capacity
        if (_cache.Count >= _maxEntries)
        {
            EvictLeastRecentlyUsed();
        }

        var entry = new CacheEntry
        {
            Embedding = embedding,
            LastAccess = Interlocked.Increment(ref _accessCounter)
        };
        _cache.TryAdd(contentHash, entry);
    }

    /// <summary>
    /// Get or compute an embedding, using cache when available.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="computeEmbedding">Function to compute embedding if not cached.</param>
    /// <returns>The embedding (cached or newly computed).</returns>
    public float[]? GetOrCompute(string text, Func<string, float[]?> computeEmbedding)
    {
        var hash = ComputeHash(text);

        if (TryGet(hash) is { } cached)
            return cached;

        var embedding = computeEmbedding(text);
        if (embedding is not null)
            Add(hash, embedding);

        return embedding;
    }

    /// <summary>
    /// Batch get or compute embeddings, optimizing for cache hits.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="computeBatch">Function to compute embeddings for uncached texts.</param>
    /// <returns>Array of embeddings in same order as input texts (null for failures).</returns>
    public float[]?[] GetOrComputeBatch(
        IReadOnlyList<string> texts,
        Func<IReadOnlyList<string>, float[]?[]> computeBatch)
    {
        var results = new float[]?[texts.Count];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();
        var hashes = new string[texts.Count];

        // First pass: check cache
        for (var i = 0; i < texts.Count; i++)
        {
            hashes[i] = ComputeHash(texts[i]);
            if (TryGet(hashes[i]) is { } cached)
            {
                results[i] = cached;
            }
            else
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(texts[i]);
            }
        }

        // Compute uncached embeddings in batch
        if (uncachedTexts.Count > 0)
        {
            var computed = computeBatch(uncachedTexts);
            for (var j = 0; j < uncachedIndices.Count; j++)
            {
                var originalIndex = uncachedIndices[j];
                var embedding = computed[j];
                results[originalIndex] = embedding;

                if (embedding is not null)
                    Add(hashes[originalIndex], embedding);
            }
        }

        return results;
    }

    /// <summary>
    /// Compute SHA256 hash for text content to use as cache key.
    /// Uses first 128 bits (32 hex chars) which is sufficient for collision resistance.
    /// </summary>
    public static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 16); // 128-bit prefix
    }

    /// <summary>Number of entries currently in cache.</summary>
    public int Count => _cache.Count;

    /// <summary>Clear all cached entries.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// Get cache statistics for diagnostics.
    /// </summary>
    public CacheStats GetStats() => new(
        EntryCount: _cache.Count,
        MaxEntries: _maxEntries,
        TotalAccesses: _accessCounter
    );

    private void EvictLeastRecentlyUsed()
    {
        // Find and remove the least recently used entry
        string? lruKey = null;
        long lruAccess = long.MaxValue;

        foreach (var kvp in _cache)
        {
            if (kvp.Value.LastAccess < lruAccess)
            {
                lruAccess = kvp.Value.LastAccess;
                lruKey = kvp.Key;
            }
        }

        if (lruKey is not null)
            _cache.TryRemove(lruKey, out _);
    }

    private sealed class CacheEntry
    {
        public required float[] Embedding { get; init; }
        public long LastAccess { get; set; }
    }
}

/// <summary>
/// Cache statistics for diagnostics.
/// </summary>
public record CacheStats(
    int EntryCount,
    int MaxEntries,
    long TotalAccesses
);
