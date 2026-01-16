using System.Collections.Concurrent;
using System.Text;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for embedding operations.
/// Provides functions to embed text and check embedding provider status.
/// Includes query-level caching to avoid redundant embedding calls for the same text.
/// </summary>
[UdfClass]
public class EmbedUdf(IEmbeddingProvider? embeddingProvider)
{
    private readonly IEmbeddingProvider? _embeddingProvider = embeddingProvider;
    /// <summary>
    /// Cache for embeddings to avoid redundant API calls.
    /// Key is (text, model), value is (embedding result, timestamp).
    /// Cache entries expire after 60 seconds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string? Result, DateTime Timestamp)> EmbeddingCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(60);
    private const int MaxCacheSize = 100;
    /// <summary>
    /// Returns status information about the embedding provider.
    /// </summary>
    /// <remarks>
    /// The dummy parameter exists because DuckDB.NET doesn't reliably support
    /// parameterless UDFs. SQL macros hide this from users.
    /// </remarks>
    [ScalarUdf("_embed_status_internal", MacroName = "embed_status", Description = "Returns status information about the embedding provider")]
    public string EmbedStatus([UdfDefault("''")] string? _dummy)
    {
        var providerType = _embeddingProvider?.GetType().Name ?? "null";
        var enabled = _embeddingProvider?.Enabled ?? false;
        var model = _embeddingProvider?.Model ?? "null";
        var dimension = _embeddingProvider?.Dimension ?? 0;

        return $"provider_type: {providerType}\nenabled: {enabled}\nmodel: {model}\ndimension: {dimension}";
    }

    /// <summary>
    /// Embeds text as a query and returns a JSON array of floats representing the embedding vector.
    /// For E5 models, this prepends "query: " prefix for optimal asymmetric search.
    /// Returns null if the embedding provider is not configured or if embedding fails.
    /// Use ::FLOAT[] in SQL to cast the result.
    /// Results are cached for 60 seconds to avoid redundant API calls.
    /// </summary>
    [ScalarUdf("embed_query", Description = "Embed search query text and return JSON array of floats (use ::FLOAT[] to cast)")]
    public string? EmbedQuery(string text)
    {
        return EmbedCore(text, "query", provider => provider.EmbedQueryAsync(text, CancellationToken.None));
    }

    /// <summary>
    /// Embeds text as a passage (document content) and returns a JSON array of floats representing the embedding vector.
    /// Prepends "passage: " prefix for asymmetric search.
    /// Returns null if the embedding provider is not configured or if embedding fails.
    /// Use ::FLOAT[] in SQL to cast the result.
    /// Results are cached for 60 seconds to avoid redundant API calls.
    /// </summary>
    [ScalarUdf("embed_passage", Description = "Embed document/passage text and return JSON array of floats (use ::FLOAT[] to cast)")]
    public string? EmbedPassage(string text)
    {
        return EmbedCore(text, "passage", provider => provider.EmbedPassageAsync(text, CancellationToken.None));
    }

    /// <summary>
    /// Core embedding implementation with caching.
    /// </summary>
    private string? EmbedCore(string text, string cacheType, Func<IEmbeddingProvider, Task<float[]?>> embedFunc)
    {
        if (_embeddingProvider is null || !_embeddingProvider.Enabled)
        {
            return null;
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Create cache key including model and type to handle provider changes
        var cacheKey = $"{_embeddingProvider.Model}:{cacheType}:{text}";

        // Check cache first
        if (EmbeddingCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Timestamp < CacheExpiry)
            {
                return cached.Result;
            }
            // Expired - remove it
            EmbeddingCache.TryRemove(cacheKey, out _);
        }

        float[]? vector;
        try
        {
            // Use single-item API instead of batch - avoids padding overhead
            // (batch pads to 256 tokens, single uses actual token count)
            vector = embedFunc(_embeddingProvider).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }

        if (vector is null)
        {
            return null;
        }

        var result = SerializeFloatArray(vector);

        // Cache the result (with basic size limit)
        if (EmbeddingCache.Count >= MaxCacheSize)
        {
            // Simple eviction: clear oldest entries
            var cutoff = DateTime.UtcNow - CacheExpiry;
            foreach (var key in EmbeddingCache.Keys)
            {
                if (EmbeddingCache.TryGetValue(key, out var entry) && entry.Timestamp < cutoff)
                {
                    EmbeddingCache.TryRemove(key, out _);
                }
            }
        }

        EmbeddingCache[cacheKey] = (result, DateTime.UtcNow);
        return result;
    }

    /// <summary>
    /// Serializes float array to JSON array format [1.0,2.0,...].
    /// Use result::FLOAT[] in SQL to convert back.
    /// </summary>
    private static string SerializeFloatArray(float[] vec)
    {
        if (vec == null || vec.Length == 0) return "[]";

        var sb = new StringBuilder(vec.Length * 10 + 2); // Pre-size for efficiency
        sb.Append('[');
        for (var i = 0; i < vec.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vec[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
