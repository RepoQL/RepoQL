using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Service for managing external repository imports (e.g., github://owner/repo).
/// Lists, adds, removes, and reindexes imported repositories.
///
/// <para><b>Purpose:</b> Enable developers to manage external code in the index
/// without using the CLI.</para>
///
/// <para><b>Complexity:</b> Queries Filesystems view for import list,
/// calls gRPC ImportRepository for add/reindex operations.</para>
/// </summary>
internal sealed class ImportsService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<ImportsService> _logger;

    public ImportsService(RepoQlConnectionManager connectionManager, ILogger<ImportsService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Get list of all external imports (excludes local file:// and help://).
    /// </summary>
    public async Task<IReadOnlyList<ImportInfo>> GetImportsAsync(CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var sql = @"
                SELECT
                    source_uri,
                    scheme,
                    file_count,
                    indexed_count,
                    embedded_count,
                    embed_pct,
                    mounted_at,
                    languages
                FROM Filesystems
                WHERE scheme NOT IN ('file', 'help')
                ORDER BY source_uri";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 100, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row =>
            {
                var fileCount = GetLong(row, 2);
                var indexedCount = GetLong(row, 3);
                var embeddedCount = GetLong(row, 4);
                var embedPct = GetDouble(row, 5);

                // Determine status based on counts
                ImportStatus status;
                if (fileCount == 0)
                {
                    status = ImportStatus.Pending;
                }
                else if (indexedCount < fileCount)
                {
                    status = ImportStatus.Indexing;
                }
                else if (embeddedCount < indexedCount && embedPct < 99)
                {
                    status = ImportStatus.Indexing;
                }
                else
                {
                    status = ImportStatus.Ready;
                }

                return new ImportInfo(
                    Uri: GetString(row, 0),
                    Scheme: GetString(row, 1),
                    FileCount: (int)fileCount,
                    IndexedCount: (int)indexedCount,
                    EmbeddedCount: (int)embeddedCount,
                    EmbedPercent: embedPct,
                    MountedAt: GetDateTimeOrNull(row, 6),
                    Languages: GetString(row, 7),
                    Status: status,
                    Error: null);
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get imports");
            return [];
        }
    }

    /// <summary>
    /// Import a new external repository.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string uri, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            _logger.LogInformation("Starting import: {Uri}", uri);

            var status = await client.ImportRepositoryAsync(uri, ct).ConfigureAwait(false);

            return new ImportResult(
                Success: true,
                Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import failed: {Uri}", uri);
            return new ImportResult(
                Success: false,
                Error: ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Remove an imported repository.
    /// </summary>
    public async Task<ImportResult> RemoveAsync(string uri, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            _logger.LogInformation("Removing import: {Uri}", uri);

            // Use negative prefix to remove
            var removeUri = uri.StartsWith('-') ? uri : $"-{uri}";

            var status = await client.ImportRepositoryAsync(removeUri, ct).ConfigureAwait(false);

            return new ImportResult(
                Success: true,
                Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remove failed: {Uri}", uri);
            return new ImportResult(
                Success: false,
                Error: ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Reindex an existing import (remove and re-add).
    /// </summary>
    public async Task<ImportResult> ReindexAsync(string uri, CancellationToken ct = default)
    {
        // Remove first
        var removeResult = await RemoveAsync(uri, ct).ConfigureAwait(false);
        if (!removeResult.Success)
        {
            // Try to import anyway - removal may fail if not present
            _logger.LogDebug("Remove before reindex failed for {Uri}, proceeding with import", uri);
        }

        // Then import
        return await ImportAsync(uri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validate an import URI.
    /// </summary>
    public static string? ValidateUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return "URI is required";

        uri = uri.Trim();

        // Must match github://owner/repo pattern
        if (!uri.StartsWith("github://", StringComparison.OrdinalIgnoreCase))
        {
            return "URI must start with github:// (e.g., github://owner/repo)";
        }

        var path = uri["github://".Length..];
        var parts = path.Split('/');

        if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
        {
            return "URI must be in format github://owner/repo";
        }

        return null;
    }

    private static string GetString(RowData row, int index)
    {
        if (index >= row.Values.Count) return "";
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue
            ? value.StringValue
            : "";
    }

    private static long GetLong(RowData row, int index)
    {
        if (index >= row.Values.Count) return 0;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (long)value.NumberValue
            : 0;
    }

    private static double GetDouble(RowData row, int index)
    {
        if (index >= row.Values.Count) return 0;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? value.NumberValue
            : 0;
    }

    private static DateTime? GetDateTimeOrNull(RowData row, int index)
    {
        if (index >= row.Values.Count) return null;
        var value = row.Values[index];
        if (value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue)
        {
            if (DateTime.TryParse(value.StringValue, out var dt))
                return dt;
        }
        return null;
    }
}

/// <summary>Status of an imported repository.</summary>
internal enum ImportStatus
{
    Pending,
    Indexing,
    Ready,
    Error
}

/// <summary>Information about an imported repository.</summary>
internal sealed record ImportInfo(
    string Uri,
    string Scheme,
    int FileCount,
    int IndexedCount,
    int EmbeddedCount,
    double EmbedPercent,
    DateTime? MountedAt,
    string Languages,
    ImportStatus Status,
    string? Error);

/// <summary>Result of an import operation.</summary>
internal sealed record ImportResult(
    bool Success,
    string? Error);
