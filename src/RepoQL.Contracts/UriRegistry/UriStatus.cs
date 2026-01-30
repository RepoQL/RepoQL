namespace RepoQL.Contracts;

/// <summary>
/// Lifecycle status of a file in the indexing pipeline.
/// </summary>
public enum UriStatus
{
    /// <summary>File discovered on disk but not yet processed.</summary>
    Discovered,

    /// <summary>File is currently being indexed.</summary>
    Indexing,

    /// <summary>File has been successfully indexed.</summary>
    Indexed,

    /// <summary>Indexing failed for this file.</summary>
    Failed,

    /// <summary>File was previously indexed but has changed and needs re-indexing.</summary>
    Stale
}
