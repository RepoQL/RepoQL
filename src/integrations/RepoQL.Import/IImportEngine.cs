namespace RepoQL.Import;

/// <summary>
/// Purpose: Orchestrate importing external data sources into the graph.
/// Complexity: Routes between VFS repository imports and SARIF annotation imports,
/// triggers post-import processing — the pure business logic of the import tool.
/// </summary>
public interface IImportEngine
{
    /// <summary>
    /// Import an external source into the graph.
    /// </summary>
    /// <param name="uri">Source URI (github://owner/repo, sarif:///path, file path).</param>
    /// <param name="options">Import options (analyze, remove, etc.).</param>
    /// <param name="cancel">Cancellation token.</param>
    Task<ImportResult> ExecuteAsync(string uri, ImportOptions? options = null, CancellationToken cancel = default);
}

public sealed class ImportOptions
{
    /// <summary>Run multi-file analysis after import.</summary>
    public bool Analyze { get; init; }

    /// <summary>Remove an existing import instead of adding.</summary>
    public bool Remove { get; init; }
}

/// <summary>
/// Transport-agnostic import result.
/// </summary>
public sealed class ImportResult
{
    public required string Uri { get; init; }
    public required ImportAction Action { get; init; }
    public string? Message { get; init; }
    public int FilesDiscovered { get; init; }
    public long ElapsedMs { get; init; }
}

public enum ImportAction
{
    Added,
    Updated,
    Removed,
    Failed
}
