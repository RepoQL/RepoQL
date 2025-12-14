namespace RepoQL.Data.DuckDB;

/// <summary>
/// Represents a persisted file system mount that survives server restarts.
/// </summary>
public sealed record FileSystemMountRecord
{
    /// <summary>
    /// Unique mount identifier, e.g., 'github:owner/repo@ref'.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// URI scheme, e.g., 'github'.
    /// </summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// URI authority (optional), e.g., 'owner'.
    /// </summary>
    public string? Authority { get; init; }

    /// <summary>
    /// Path prefix for URI matching, e.g., 'repo'.
    /// </summary>
    public required string PathPrefix { get; init; }

    /// <summary>
    /// Original import URI.
    /// </summary>
    public required string SourceUri { get; init; }

    /// <summary>
    /// Physical path on disk where the content is stored.
    /// </summary>
    public required string LocalPath { get; init; }

    /// <summary>
    /// When the mount was created.
    /// </summary>
    public DateTimeOffset? MountedAt { get; init; }

    /// <summary>
    /// Whether to include this mount in full repository scans.
    /// </summary>
    public bool IncludeInEnumeration { get; init; } = true;

    /// <summary>
    /// Whether to watch for file changes (typically false for imports).
    /// </summary>
    public bool EnableWatching { get; init; }

    /// <summary>
    /// Whether to run analyzers on this mount (typically false for imports).
    /// </summary>
    public bool EnableAnalysis { get; init; }
}
