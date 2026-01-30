using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for querying the URI registry from SQL.
///
/// Purpose: Expose URI registry state (file status, embedding status, scope readiness)
/// to SQL queries for observability and scope validation.
///
/// Complexity: Delegates to UriRegistry extension methods. Returns structured results
/// that can be consumed as tables or scalar values.
/// </summary>
[UdfClass]
public class UriRegistryUdf
{
    private readonly UriRegistry _registry;

    public UriRegistryUdf(UriRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Returns files matching a pattern with their status.
    /// </summary>
    [StructuredUdf("_indexer_status_internal", Description = "Returns file status from URI registry")]
    public IEnumerable<IndexerStatusRow> IndexerStatus(
        [UdfDefault("NULL")] string? pattern)
    {
        var files = string.IsNullOrWhiteSpace(pattern)
            ? _registry.FileEntries
            : _registry.MatchFiles(pattern).Select(uri => new KeyValuePair<RepoUri, FileEntry>(uri, _registry[uri]));

        foreach (var (uri, entry) in files)
        {
            yield return new IndexerStatusRow(
                uri.AbsoluteUri,
                entry.Status.ToString(),
                entry.IndexedAt?.ToString("O"),
                entry.Error,
                entry.EmbeddingStatus.ToString(),
                entry.EmbeddedChunkCount,
                entry.EmbeddedAt?.ToString("O"),
                entry.Symbols.Count
            );
        }
    }

    /// <summary>
    /// Returns scope readiness information for a pattern.
    /// </summary>
    [StructuredUdf("_scope_readiness_internal", Description = "Returns scope readiness for semantic search")]
    public IEnumerable<ScopeReadinessRow> ScopeReadiness(
        [UdfDefault("NULL")] string? pattern)
    {
        var readiness = _registry.CheckScope(pattern);

        yield return new ScopeReadinessRow(
            readiness.IsReady,
            readiness.TotalFiles,
            readiness.IndexedCount,
            readiness.EmbeddedCount,
            readiness.PendingIndex.Count,
            readiness.PendingEmbedding.Count,
            readiness.FailedFiles.Count,
            readiness.ReadyPercent,
            readiness.Summary
        );
    }

    /// <summary>
    /// Returns files pending indexing.
    /// </summary>
    [StructuredUdf("_indexer_pending_internal", Description = "Returns files pending indexing")]
    public IEnumerable<PendingRow> IndexerPending(
        [UdfDefault("NULL")] string? pattern)
    {
        var files = string.IsNullOrWhiteSpace(pattern)
            ? _registry.FileEntries
            : _registry.MatchFiles(pattern).Select(uri => new KeyValuePair<RepoUri, FileEntry>(uri, _registry[uri]));

        foreach (var (uri, entry) in files)
        {
            if (entry.Status != UriStatus.Indexed)
            {
                yield return new PendingRow(uri.AbsoluteUri, entry.Status.ToString());
            }
        }
    }

    /// <summary>
    /// Returns files pending embedding.
    /// </summary>
    [StructuredUdf("_embedding_pending_internal", Description = "Returns files pending embedding")]
    public IEnumerable<EmbeddingPendingRow> EmbeddingPending(
        [UdfDefault("NULL")] string? pattern)
    {
        var files = string.IsNullOrWhiteSpace(pattern)
            ? _registry.FileEntries
            : _registry.MatchFiles(pattern).Select(uri => new KeyValuePair<RepoUri, FileEntry>(uri, _registry[uri]));

        foreach (var (uri, entry) in files)
        {
            if (entry.Status == UriStatus.Indexed &&
                entry.EmbeddingStatus != EmbeddingStatus.Embedded &&
                entry.EmbeddingStatus != EmbeddingStatus.NotApplicable)
            {
                yield return new EmbeddingPendingRow(uri.AbsoluteUri, entry.EmbeddingStatus.ToString());
            }
        }
    }

    /// <summary>
    /// Returns failed files with their errors.
    /// </summary>
    [StructuredUdf("_indexer_errors_internal", Description = "Returns failed files with errors")]
    public IEnumerable<ErrorRow> IndexerErrors(
        [UdfDefault("NULL")] string? pattern)
    {
        var files = string.IsNullOrWhiteSpace(pattern)
            ? _registry.FileEntries
            : _registry.MatchFiles(pattern).Select(uri => new KeyValuePair<RepoUri, FileEntry>(uri, _registry[uri]));

        foreach (var (uri, entry) in files)
        {
            if (entry.Status == UriStatus.Failed ||
                entry.EmbeddingStatus == EmbeddingStatus.Failed)
            {
                yield return new ErrorRow(uri.AbsoluteUri, entry.Status.ToString(), entry.Error);
            }
        }
    }

    /// <summary>
    /// Returns registry summary statistics.
    /// </summary>
    [StructuredUdf("_registry_summary_internal", Description = "Returns registry summary statistics")]
    public IEnumerable<RegistrySummaryRow> RegistrySummary()
    {
        var summary = _registry.GetSummary();

        yield return new RegistrySummaryRow(
            summary.TotalFiles,
            summary.TotalSymbols,
            summary.ByStatus.GetValueOrDefault(UriStatus.Discovered),
            summary.ByStatus.GetValueOrDefault(UriStatus.Indexing),
            summary.ByStatus.GetValueOrDefault(UriStatus.Indexed),
            summary.ByStatus.GetValueOrDefault(UriStatus.Failed),
            summary.ByStatus.GetValueOrDefault(UriStatus.Stale),
            summary.ByEmbeddingStatus.GetValueOrDefault(EmbeddingStatus.Pending),
            summary.ByEmbeddingStatus.GetValueOrDefault(EmbeddingStatus.Embedding),
            summary.ByEmbeddingStatus.GetValueOrDefault(EmbeddingStatus.Embedded),
            summary.ByEmbeddingStatus.GetValueOrDefault(EmbeddingStatus.Failed),
            summary.ByEmbeddingStatus.GetValueOrDefault(EmbeddingStatus.NotApplicable)
        );
    }

    /// <summary>
    /// Checks if a scope is ready for semantic search. Returns true/false.
    /// </summary>
    [ScalarUdf("is_scope_ready", IsPure = false)]
    public string IsScopeReady([UdfDefault("NULL")] string? pattern)
    {
        var readiness = _registry.CheckScope(pattern);
        return readiness.IsReady ? "true" : "false";
    }

    // Result record types
    public record IndexerStatusRow(
        string Uri,
        string Status,
        string? IndexedAt,
        string? Error,
        string EmbeddingStatus,
        int EmbeddedChunks,
        string? EmbeddedAt,
        int SymbolCount
    );

    public record ScopeReadinessRow(
        bool IsReady,
        int TotalFiles,
        int IndexedCount,
        int EmbeddedCount,
        int PendingIndex,
        int PendingEmbedding,
        int FailedCount,
        int ReadyPercent,
        string Summary
    );

    public record PendingRow(string Uri, string Status);

    public record EmbeddingPendingRow(string Uri, string EmbeddingStatus);

    public record ErrorRow(string Uri, string Status, string? Error);

    public record RegistrySummaryRow(
        int TotalFiles,
        int TotalSymbols,
        int Discovered,
        int Indexing,
        int Indexed,
        int Failed,
        int Stale,
        int PendingEmbed,
        int Embedding,
        int Embedded,
        int EmbedFailed,
        int NotApplicable
    );
}
