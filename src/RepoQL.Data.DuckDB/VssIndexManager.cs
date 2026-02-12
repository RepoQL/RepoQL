using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Abstraction for refreshing in-memory VSS indexes.
/// </summary>
public interface IVssIndexManager
{
    Task RefreshIndexesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages ephemeral HNSW (Hierarchical Navigable Small Worlds) indexes for vector similarity search.
///
/// Purpose: Builds in-memory HNSW indexes from the persistent document_embedding table to
/// accelerate semantic search from ~15s (linear scan) to &lt;1s (approximate nearest neighbor).
///
/// Complexity: DuckDB's VSS extension requires fixed-size ARRAY types, but our embeddings
/// use variable-size LIST types. This class creates dimension-specific tables that cast
/// embeddings to the appropriate ARRAY size and builds HNSW indexes on them. The indexes
/// are ephemeral (in-memory only) to avoid VSS persistence bugs.
/// </summary>
public sealed class VssIndexManager : IVssIndexManager
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.VssIndex");

    /// <summary>
    /// Supported embedding dimensions. Each dimension gets its own HNSW index table.
    /// </summary>
    private static readonly int[] SupportedDimensions = [384, 768, 1024];

    private readonly DuckDbDataStore _store;
    private readonly ILogger _logger;
    private long _lastEmbeddingCount;
    private DateTime _lastRefreshTime = DateTime.MinValue;

    /// <summary>
    /// Minimum time between index refreshes to avoid thrashing.
    /// </summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    public VssIndexManager(DuckDbDataStore store, ILogger<VssIndexManager>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<VssIndexManager>.Instance;
    }

    /// <summary>
    /// Check if VSS extension is available.
    /// </summary>
    public bool IsVssAvailable()
    {
        try
        {
            var result = _store.ReadScalar<long>(
                "SELECT COUNT(*) FROM duckdb_extensions() WHERE extension_name = 'vss' AND loaded = true");
            return result > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Refresh HNSW indexes for all dimensions that have embeddings.
    /// Only rebuilds if embeddings have changed since the last refresh.
    /// </summary>
    public async Task RefreshIndexesAsync(CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("vss.refresh_indexes", ActivityKind.Internal);

        if (!IsVssAvailable())
        {
            activity?.SetTag("vss.available", false);
            _logger.LogDebug("VSS extension not available, skipping index refresh");
            return;
        }

        // Check if we should skip refresh (too soon or no changes)
        var currentCount = GetEmbeddingCount();
        var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;

        if (timeSinceLastRefresh < MinRefreshInterval && currentCount == _lastEmbeddingCount)
        {
            activity?.SetTag("vss.skipped", "no_changes");
            return;
        }

        activity?.SetTag("vss.available", true);
        activity?.SetTag("vss.embedding_count", currentCount);

        // Find which dimensions have embeddings
        var dims = _store.Read<int>(
            "SELECT DISTINCT dim FROM document_embedding WHERE scope = 'document' AND embedding IS NOT NULL",
            r => r.GetInt32(0));

        var supportedDims = dims.Where(d => SupportedDimensions.Contains(d)).ToList();
        activity?.SetTag("vss.dimensions", string.Join(",", supportedDims));

        var indexedCount = 0;
        foreach (var dim in supportedDims)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var count = await RefreshDimensionIndexAsync(dim, cancellationToken);
                indexedCount += count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create HNSW index for dimension {Dim}", dim);
            }
        }

        _lastEmbeddingCount = currentCount;
        _lastRefreshTime = DateTime.UtcNow;

        activity?.SetTag("vss.indexed_count", indexedCount);
        _logger.LogDebug("VSS index refresh complete: {Count} embeddings indexed across {DimCount} dimensions",
            indexedCount, supportedDims.Count);
    }

    /// <summary>
    /// Create or refresh the HNSW index for a specific dimension.
    /// The VSS tables are pre-created by schema (vss_indexes.sql), so we just
    /// truncate and repopulate, then recreate the HNSW index.
    /// </summary>
    private async Task<int> RefreshDimensionIndexAsync(int dim, CancellationToken cancellationToken)
    {
        var tableName = GetIndexTableName(dim);
        var indexName = $"{tableName}_hnsw";

        using var activity = ActivitySource.StartActivity("vss.refresh_dimension", ActivityKind.Internal);
        activity?.SetTag("vss.dimension", dim);
        activity?.SetTag("vss.table", tableName);

        var sw = Stopwatch.StartNew();

        // Truncate and repopulate the VSS table, then create HNSW index
        // The table already exists (created by schema), we just refresh its contents
        var sql = $@"
            DROP INDEX IF EXISTS {indexName};
            DELETE FROM {tableName};
            INSERT INTO {tableName} (node_id, doc_id, embedding_type, vec)
            SELECT
                node_id,
                doc_id,
                embedding_type,
                embedding::FLOAT[{dim}] AS vec
            FROM document_embedding
            WHERE scope = 'document'
              AND dim = {dim}
              AND embedding IS NOT NULL;
            CREATE INDEX {indexName} ON {tableName} USING HNSW (vec) WITH (metric = 'cosine');
        ";

        await Task.Run(() => _store.ExecuteRaw(sql), cancellationToken);

        // Get row count
        var count = _store.ReadScalar<long>($"SELECT COUNT(*) FROM {tableName}");

        sw.Stop();
        activity?.SetTag("vss.row_count", count);
        activity?.SetTag("vss.build_time_ms", sw.ElapsedMilliseconds);

        _logger.LogDebug("Built HNSW index for dim={Dim}: {Count} vectors in {Ms}ms",
            dim, count, sw.ElapsedMilliseconds);

        return (int)count;
    }

    /// <summary>
    /// Check if an HNSW index exists for the given dimension.
    /// </summary>
    public bool HasIndexForDimension(int dim)
    {
        var tableName = GetIndexTableName(dim);
        try
        {
            var count = _store.ReadScalar<long>(
                $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableName}'");
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the index table name for a given dimension.
    /// </summary>
    public static string GetIndexTableName(int dim) => $"_vss_index_{dim}";

    /// <summary>
    /// Get the current embedding count for change detection.
    /// </summary>
    private long GetEmbeddingCount()
    {
        try
        {
            return _store.ReadScalar<long>(
                "SELECT COUNT(*) FROM document_embedding WHERE scope = 'document'");
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Get status information about VSS indexes for diagnostics.
    /// </summary>
    public VssIndexStatus GetStatus()
    {
        var isAvailable = IsVssAvailable();
        var indexes = new List<(int Dim, long Count)>();

        if (isAvailable)
        {
            foreach (var dim in SupportedDimensions)
            {
                var tableName = GetIndexTableName(dim);
                try
                {
                    var exists = _store.ReadScalar<long>(
                        $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableName}'") > 0;
                    if (exists)
                    {
                        var count = _store.ReadScalar<long>($"SELECT COUNT(*) FROM {tableName}");
                        indexes.Add((dim, count));
                    }
                }
                catch
                {
                    // Ignore - table doesn't exist or error
                }
            }
        }

        return new VssIndexStatus(isAvailable, indexes, _lastRefreshTime);
    }
}

/// <summary>
/// Status information about VSS indexes.
/// </summary>
public record VssIndexStatus(
    bool IsVssAvailable,
    IReadOnlyList<(int Dimension, long Count)> Indexes,
    DateTime LastRefreshTime);
