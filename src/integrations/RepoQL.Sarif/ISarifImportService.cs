using RepoQL.Sarif.Models;

namespace RepoQL.Sarif;

/// <summary>
/// Imports SARIF files into source-scoped lint annotations.
/// </summary>
public interface ISarifImportService
{
    /// <summary>
    /// Import a SARIF file from disk.
    /// </summary>
    Task<SarifImportResult> ImportAsync(
        string sarifFilePath,
        CancellationToken cancellationToken = default);
}
