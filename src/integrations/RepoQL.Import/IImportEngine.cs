namespace RepoQL.Import;

/// <summary>
/// Purpose: Orchestrate importing external data sources into the graph.
/// Complexity: Routes between VFS repository imports and SARIF annotation imports,
/// assembles results — the pure business logic of the import tool.
/// </summary>
public interface IImportEngine
{
    /// <summary>
    /// Import an external source into the graph.
    /// </summary>
    Task<ImportResult> ExecuteAsync(ImportRequest request, CancellationToken cancel = default);
}

/// <summary>
/// Abstraction for importing a VFS-backed repository (clone/sync).
/// </summary>
public interface IRepositoryImporter
{
    Task<RepositoryImportResult> ImportAsync(string uri, bool analyze, CancellationToken cancel);
    Task<RemoveImportResult> RemoveAsync(string uri, CancellationToken cancel);
}

/// <summary>
/// Abstraction for importing SARIF analysis results as annotations.
/// </summary>
public interface ISarifImporter
{
    Task<SarifImportResult> ImportAsync(string filePath, CancellationToken cancel);
}

public sealed class ImportRequest
{
    public required string Uri { get; init; }
    public bool Analyze { get; init; }
}

public sealed class ImportResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public required ImportAction Action { get; init; }
    public string? Message { get; init; }
    public int TotalFiles { get; init; }
    public int IndexedCount { get; init; }
    public int FailedCount { get; init; }
    public string? OperationId { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class RepositoryImportResult
{
    public string? OperationId { get; init; }
    public int TotalFiles { get; init; }
    public int IndexedCount { get; init; }
    public int FailedCount { get; init; }
}

public sealed class RemoveImportResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
}

public sealed class SarifImportResult
{
    public int RulesImported { get; init; }
    public int AnnotationsCreated { get; init; }
    public int AnnotationsRemoved { get; init; }
    public string? Message { get; init; }
}

public enum ImportAction
{
    Added,
    Updated,
    Removed,
    Failed
}
