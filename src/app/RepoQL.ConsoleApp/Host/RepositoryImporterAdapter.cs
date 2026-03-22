using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Import;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.FileSystems.Imports;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Adapt host-level repository import services to the <see cref="IRepositoryImporter"/> interface.
/// Complexity: Delegates clone/sync to <see cref="IFileSystemImportService"/>, removal to
/// direct DuckDB queries plus mount manager cleanup. Bridges the gap between the transport-agnostic
/// import engine and the host's concrete services.
/// </summary>
internal sealed class RepositoryImporterAdapter(
    IFileSystemImportService importService,
    ICompositeFileSystemManager mountManager,
    DuckDbDataStore db,
    ILogger<RepositoryImporterAdapter>? logger = null) : IRepositoryImporter
{
    private readonly ILogger _logger = logger ?? NullLogger<RepositoryImporterAdapter>.Instance;

    public async Task<RepositoryImportResult> ImportAsync(string uri, bool analyze, CancellationToken cancel)
    {
        var repoUri = RepoUri.Parse(uri);
        var result = await importService.ImportAsync(repoUri, analyze, cancel).ConfigureAwait(false);
        db.TryCheckpoint();
        var progress = result.Operation?.Progress;
        return new RepositoryImportResult
        {
            OperationId = result.Operation?.Id,
            Operation = result.Operation,
            TotalFiles = progress?.TotalFiles ?? 0,
            IndexedCount = progress?.IndexedCount ?? 0,
            FailedCount = progress?.FailedCount ?? 0
        };
    }

    public Task<RemoveImportResult> RemoveAsync(string uri, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return Task.FromResult(new RemoveImportResult
            {
                Success = false,
                Message = "URI is required for removal."
            });
        }

        _logger.LogDebug("[Import:Remove] Searching for mount matching '{Uri}'", uri);

        var mounts = db.GetAllMounts();
        _logger.LogDebug("[Import:Remove] Found {Count} total mounts to search", mounts.Count);

        var matchingMount = mounts.FirstOrDefault(m =>
            m.SourceUri.Equals(uri, StringComparison.OrdinalIgnoreCase) ||
            m.Id.Contains(uri.Replace("://", ":"), StringComparison.OrdinalIgnoreCase));

        if (matchingMount is null)
        {
            _logger.LogWarning("[Import:Remove] No mount found matching '{Uri}'. Available mounts: {Mounts}",
                uri, string.Join(", ", mounts.Select(m => m.Id)));
            return Task.FromResult(new RemoveImportResult
            {
                Success = false,
                Message = $"No import found matching: {uri}"
            });
        }

        _logger.LogInformation("[Import:Remove] Found mount {MountId} (source: {SourceUri}, local: {LocalPath})",
            matchingMount.Id, matchingMount.SourceUri, matchingMount.LocalPath);

        // Build URI pattern for matching documents
        var docPattern = string.IsNullOrEmpty(matchingMount.Authority)
            ? $"{matchingMount.Scheme}:///{matchingMount.PathPrefix}%"
            : $"{matchingMount.Scheme}://{matchingMount.Authority}/{matchingMount.PathPrefix}%";

        _logger.LogDebug("[Import:Remove] Querying documents with pattern '{Pattern}'", docPattern);

        var docUris = db.Read(
            $"SELECT uri FROM node WHERE kind = 'document' AND uri LIKE '{EscapeSqlLiteral(docPattern)}'",
            r => r.GetString(0));

        _logger.LogInformation("[Import:Remove] Found {Count} documents to delete", docUris.Count);

        var deleted = 0;
        foreach (var docUri in docUris)
        {
            if (RepoUri.TryParse(docUri, out var repoUri))
            {
                db.DeleteArtifact(repoUri);
                deleted++;
                if (deleted % 100 == 0)
                    _logger.LogDebug("[Import:Remove] Deleted {Count}/{Total} documents", deleted, docUris.Count);
            }
        }

        _logger.LogInformation("[Import:Remove] Deleted {Count} documents", deleted);

        // Remove indexed git history for this source
        var historyPrefix = BuildMountHistoryPrefix(matchingMount);
        db.ExecuteRaw(
            $"""
            DELETE FROM git_file_change
            WHERE starts_with(uri, '{historyPrefix}')
               OR (old_uri IS NOT NULL AND starts_with(old_uri, '{historyPrefix}'));

            DELETE FROM git_commit
            WHERE hash NOT IN (SELECT DISTINCT commit_hash FROM git_file_change);
            """);
        _logger.LogInformation("[Import:Remove] Deleted git history rows matching prefix '{Prefix}'", historyPrefix);

        db.DeleteMount(matchingMount.Id);
        mountManager.RemoveMount(matchingMount.Id);

        _logger.LogInformation("[Import:Remove] Completed removal of {MountId} ({Count} documents)",
            matchingMount.Id, deleted);

        return Task.FromResult(new RemoveImportResult
        {
            Success = true,
            Message = $"Removed {deleted} documents from mount {matchingMount.Id}"
        });
    }

    private static string BuildMountHistoryPrefix(FileSystemMountRecord mount)
    {
        var sourceUri = BuildMountSourceUri(mount).TrimEnd('/');
        return EscapeSqlLiteral($"{sourceUri}/");
    }

    private static string BuildMountSourceUri(FileSystemMountRecord mount)
    {
        var scheme = (mount.Scheme ?? string.Empty).Trim().ToLowerInvariant();
        var authority = mount.Authority?.Trim();
        var pathPrefix = (mount.PathPrefix ?? string.Empty).Trim('/').Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(authority))
            return string.IsNullOrWhiteSpace(pathPrefix)
                ? $"{scheme}://"
                : $"{scheme}:///{pathPrefix}";

        return string.IsNullOrWhiteSpace(pathPrefix)
            ? $"{scheme}://{authority}"
            : $"{scheme}://{authority}/{pathPrefix}";
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
