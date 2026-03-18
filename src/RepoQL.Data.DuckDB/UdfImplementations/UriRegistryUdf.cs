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
/// that can be consumed as tables or scalar values. Normalizes absolute paths in patterns
/// to repo-relative URIs when repo root is available.
/// </summary>
[UdfClass]
public class UriRegistryUdf
{
    private readonly UriRegistry _registry;
    private readonly string? _repoRoot;

    public UriRegistryUdf(UriRegistry registry, RepositoryConfiguration repoConfig)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repoRoot = repoConfig?.Path;
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
                entry.Symbols.Count,
                entry.ProcessingDurationMs
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
            if (entry.Status != UriStatus.Indexed &&
                entry.Status != UriStatus.Failed &&
                entry.Status != UriStatus.Skipped)
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
    [StructuredUdf("_indexer_errors_internal", MacroName = "failed_files", Description = "Returns failed files with errors")]
    public IEnumerable<ErrorRow> IndexerErrors(
        [UdfDefault("NULL")] string? pattern)
    {
        var files = string.IsNullOrWhiteSpace(pattern)
            ? _registry.FileEntries
            : _registry.MatchFiles(pattern).Select(uri => new KeyValuePair<RepoUri, FileEntry>(uri, _registry[uri]));

        foreach (var (uri, entry) in files)
        {
            if (entry.Status == UriStatus.Failed ||
                entry.Status == UriStatus.Skipped ||
                entry.EmbeddingStatus == EmbeddingStatus.Failed)
            {
                yield return new ErrorRow(uri.AbsoluteUri, entry.Status.ToString(), entry.Error, entry.ProcessingDurationMs);
            }
        }
    }

    /// <summary>
    /// Returns registry summary statistics.
    /// </summary>
    [StructuredUdf("_registry_summary_internal", Description = "Returns registry summary statistics")]
    public IEnumerable<RegistrySummaryRow> RegistrySummary(
        [UdfDefault("''")] string? _dummy)
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

    /// <summary>
    /// Returns URIs matching a pattern using line-range-based globbing.
    /// Supports compound patterns, negations, symbol wildcards, and line range exclusions.
    /// Normalizes absolute paths in patterns to repo-relative URIs when repo root is available.
    /// </summary>
    /// <example>
    /// SELECT * FROM _glob_files_internal('src/**/*.cs')
    /// SELECT * FROM _glob_files_internal('src/**/*.cs#symbol=*')
    /// SELECT * FROM _glob_files_internal('src/**/*.cs#symbol=*;!#line=1,30')
    /// SELECT * FROM _glob_files_internal('C:/Source/Repo/src/**/*.cs')  -- absolute path normalized
    /// </example>
    [StructuredUdf("_glob_files_internal", Description = "Returns URIs matching pattern from registry using line-range globbing")]
    public IEnumerable<GlobResult> GlobFilesInternal([UdfDefault("NULL")] string? pattern)
    {
        // Normalize pattern to convert absolute paths to repo-relative (when repo root is available)
        var normalizedPattern = _repoRoot != null
            ? GlobPatternNormalizer.NormalizePattern(pattern, _repoRoot)
            : pattern;

        foreach (var uri in _registry.MatchPattern(normalizedPattern))
        {
            yield return new GlobResult(uri.AbsoluteUri);
        }
    }

    // Result record types
    public record GlobResult(string Uri);
    public record IndexerStatusRow(
        string Uri,
        string Status,
        string? IndexedAt,
        string? Error,
        string EmbeddingStatus,
        int EmbeddedChunks,
        string? EmbeddedAt,
        int SymbolCount,
        [property: System.Text.Json.Serialization.JsonPropertyName("processing_duration_ms")]
        long? ProcessingDurationMs
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

    public record ErrorRow(
        string Uri,
        string Status,
        string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("processing_duration_ms")]
        long? ProcessingDurationMs
    );

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
