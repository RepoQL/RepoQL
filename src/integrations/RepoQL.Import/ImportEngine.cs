using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.Import;

/// <summary>
/// Purpose: Route import requests to the correct service (VFS repository or SARIF annotations).
/// Complexity: URI parsing, removal detection (- prefix), delegation to typed importers.
/// No transport knowledge — pure business logic.
/// </summary>
public sealed class ImportEngine : IImportEngine
{
    private readonly IRepositoryImporter _repoImporter;
    private readonly ISarifImporter _sarifImporter;
    private readonly ILogger _logger;

    public ImportEngine(
        IRepositoryImporter repoImporter,
        ISarifImporter sarifImporter,
        ILogger<ImportEngine>? logger = null)
    {
        _repoImporter = repoImporter;
        _sarifImporter = sarifImporter;
        _logger = logger ?? NullLogger<ImportEngine>.Instance;
    }

    public async Task<ImportResult> ExecuteAsync(ImportRequest request, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        var uri = request.Uri.Trim();

        if (string.IsNullOrWhiteSpace(uri))
            return Fail("uri is required.", sw);

        // Handle removal with '-' prefix
        var isRemoval = uri.StartsWith('-');
        var displayUri = isRemoval ? uri[1..].Trim() : uri;

        _logger.LogInformation("[Import] Starting {Operation} for {Uri}",
            isRemoval ? "removal" : "import", displayUri);

        if (isRemoval)
        {
            var removeResult = await _repoImporter.RemoveAsync(displayUri, cancel).ConfigureAwait(false);
            return new ImportResult
            {
                Success = removeResult.Success,
                Action = ImportAction.Removed,
                Message = removeResult.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        if (!RepoUri.TryParse(uri, out var repoUri))
            return Fail($"Invalid Repo URI '{uri}'.", sw);

        // Route: SARIF import
        if (string.Equals(repoUri!.Scheme, "sarif", StringComparison.OrdinalIgnoreCase))
        {
            var sarifResult = await _sarifImporter.ImportAsync(
                repoUri.AbsolutePath, cancel).ConfigureAwait(false);

            return new ImportResult
            {
                Success = true,
                Action = ImportAction.Added,
                Message = sarifResult.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        // Route: Repository VFS import
        var result = await _repoImporter.ImportAsync(displayUri, request.Analyze, cancel).ConfigureAwait(false);

        return new ImportResult
        {
            Success = true,
            Action = ImportAction.Added,
            Message = result.OperationId is not null
                ? $"Importing {result.TotalFiles} files from {displayUri} - operation {result.OperationId}"
                : $"Import started for {displayUri}. Operation tracking is unavailable.",
            TotalFiles = result.TotalFiles,
            IndexedCount = result.IndexedCount,
            FailedCount = result.FailedCount,
            OperationId = result.OperationId,
            Operation = result.Operation,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private static ImportResult Fail(string error, Stopwatch sw) => new()
    {
        Success = false,
        Error = error,
        Action = ImportAction.Failed,
        ElapsedMs = sw.ElapsedMilliseconds
    };
}
