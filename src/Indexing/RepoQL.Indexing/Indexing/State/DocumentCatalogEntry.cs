using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoQL.Contracts;

namespace RepoQL.Indexing.Indexing.State;

/// <summary>
/// Immutable snapshot of a document that has been indexed and committed to storage.
/// </summary>
public sealed record DocumentCatalogEntry(
    RepoUri Uri,
    string Digest,
    SemanticMediaType MediaType,
    string? PhysicalPath,
    DateTimeOffset LastModifiedUtc);

/// <summary>
/// Describes how to refresh catalog entries from persistent storage on startup.
/// </summary>
public interface IDocumentCatalogDataSource
{
    /// <summary>
    /// Hydrates the catalog from persistent storage.
    /// </summary>
    Task<IReadOnlyList<DocumentCatalogEntry>> LoadAsync(CancellationToken cancellationToken);
}

public sealed class NullDocumentCatalogDataSource : IDocumentCatalogDataSource
{
    public static NullDocumentCatalogDataSource Instance { get; } = new();

    public Task<IReadOnlyList<DocumentCatalogEntry>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DocumentCatalogEntry>>(Array.Empty<DocumentCatalogEntry>());
}
