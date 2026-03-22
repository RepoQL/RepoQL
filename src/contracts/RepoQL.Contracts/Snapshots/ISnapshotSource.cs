using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Snapshots;

/// <summary>
/// Provides pre-computed graph data that can be loaded into the store on startup.
/// Implementations supply versioned document batches; the loader skips unchanged versions.
/// </summary>
public interface ISnapshotSource
{
    /// <summary>
    /// Stable identifier for this source (e.g., "help-docs"). Used as the metadata key.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Version string. When this changes, the loader deletes stale data and reloads.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// URI prefix for cleanup (e.g., "help://"). All data matching this prefix is deleted before reload.
    /// </summary>
    string UriPrefix { get; }

    /// <summary>
    /// Returns the pre-computed documents to load.
    /// </summary>
    IReadOnlyList<SnapshotDocument> GetDocuments();
}

/// <summary>
/// A single document's worth of pre-computed graph data, ready for store insertion.
/// </summary>
public sealed class SnapshotDocument
{
    /// <summary>
    /// The document URI.
    /// </summary>
    public required RepoUri Uri { get; init; }

    /// <summary>
    /// The complete Records for this document (artifact, nodes, spans, edges, annotations).
    /// </summary>
    public required Records Records { get; init; }

    /// <summary>
    /// Optional pre-computed embeddings. When null, the idle embedding refresher picks these up later.
    /// </summary>
    public IReadOnlyList<DocumentEmbedding>? Embeddings { get; init; }
}
