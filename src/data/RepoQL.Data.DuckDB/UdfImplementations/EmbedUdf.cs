using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for embedding operations.
/// Provides functions to embed text and check embedding provider status.
/// Includes query-level caching to avoid redundant embedding calls for the same text.
/// When a contextual embedding provider is available, query embeddings use it
/// to match the dimension of contextual document embeddings.
/// </summary>
[UdfClass]
public class EmbedUdf(
    IEmbeddingProvider? embeddingProvider,
    IContextualEmbeddingProvider? contextualProvider = null,
    ILogger<EmbedUdf>? logger = null)
{
    private readonly IEmbeddingProvider? _embeddingProvider = embeddingProvider;
    private readonly IContextualEmbeddingProvider? _contextualProvider = contextualProvider is { Enabled: true } ? contextualProvider : null;
    private readonly ILogger<EmbedUdf>? _logger = logger;
    /// <summary>
    /// Cache for embeddings to avoid redundant API calls.
    /// Key is (text, model), value is (embedding result, timestamp).
    /// Cache entries expire after 60 seconds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string? Result, DateTime Timestamp)> EmbeddingCache = new();
    private static string? _lastEmbedError;

    [ScalarUdf("embed_last_error", Description = "Returns the last embed_query error (if any)", IsPure = false)]
    public string? EmbedLastError([UdfDefault("''")] string? _dummy) => _lastEmbedError ?? "no error";
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(60);
    private const int MaxCacheSize = 100;
    /// <summary>
    /// Returns status information about the embedding provider.
    /// </summary>
    /// <remarks>
    /// The dummy parameter exists because DuckDB.NET doesn't reliably support
    /// parameterless UDFs. SQL macros hide this from users.
    /// </remarks>
    [ScalarUdf("_embed_status_internal", MacroName = "embed_status", Description = "Returns status information about the embedding provider", IsPure = true)]
    public string EmbedStatus([UdfDefault("''")] string? _dummy)
    {
        var flatType = _embeddingProvider?.GetType().Name ?? "null";
        var flatEnabled = _embeddingProvider?.Enabled ?? false;
        var flatModel = _embeddingProvider?.Model ?? "null";
        var flatDim = _embeddingProvider?.Dimension ?? 0;

        var sb = new StringBuilder();
        sb.AppendLine($"flat_provider: {flatType}");
        sb.AppendLine($"flat_enabled: {flatEnabled}");
        sb.AppendLine($"flat_model: {flatModel}");
        sb.AppendLine($"flat_dimension: {flatDim}");

        if (_contextualProvider is not null)
        {
            sb.AppendLine($"contextual_provider: {_contextualProvider.GetType().Name}");
            sb.AppendLine($"contextual_model: {_contextualProvider.Model}");
            sb.AppendLine($"contextual_dimension: {_contextualProvider.Dimension}");
            sb.AppendLine("active_query_provider: contextual");
        }
        else
        {
            sb.AppendLine("active_query_provider: flat");
        }

        return sb.ToString().TrimEnd();
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
        // Prefer contextual provider so query vectors match contextual document vectors.
        // Fall back to flat provider if contextual call fails (e.g., service not running).
        if (_contextualProvider is not null)
        {
            var result = EmbedCoreContextual(text, "query", ct => _contextualProvider.EmbedQueryAsync(text, ct));
            if (result is not null)
                return result;
        }

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
    /// Core embedding via contextual provider (for query vectors that must match contextual document vectors).
    /// </summary>
    private string? EmbedCoreContextual(string text, string cacheType, Func<CancellationToken, Task<float[]?>> embedFunc)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var cacheKey = $"{_contextualProvider!.Model}:{cacheType}:{text}";
        return EmbedWithCache(cacheKey, () => embedFunc(CancellationToken.None));
    }

    /// <summary>
    /// Core embedding via flat provider.
    /// </summary>
    private string? EmbedCore(string text, string cacheType, Func<IEmbeddingProvider, Task<float[]?>> embedFunc)
    {
        if (_embeddingProvider is null || !_embeddingProvider.Enabled)
            return null;

        if (string.IsNullOrEmpty(text))
            return null;

        var cacheKey = $"{_embeddingProvider.Model}:{cacheType}:{text}";
        return EmbedWithCache(cacheKey, () => embedFunc(_embeddingProvider));
    }

    private string? EmbedWithCache(string cacheKey, Func<Task<float[]?>> embedFunc)
    {
        if (EmbeddingCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Timestamp < CacheExpiry)
                return cached.Result;
            EmbeddingCache.TryRemove(cacheKey, out _);
        }

        float[]? vector;
        try
        {
            vector = embedFunc().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _lastEmbedError = $"{ex.GetType().Name}: {ex.Message}";
            _logger?.LogWarning(ex, "embed_query failed, falling back to flat provider");
            return null;
        }

        if (vector is null)
            return null;

        var result = SerializeFloatArray(vector);

        if (EmbeddingCache.Count >= MaxCacheSize)
        {
            var cutoff = DateTime.UtcNow - CacheExpiry;
            foreach (var key in EmbeddingCache.Keys)
            {
                if (EmbeddingCache.TryGetValue(key, out var entry) && entry.Timestamp < cutoff)
                    EmbeddingCache.TryRemove(key, out _);
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
