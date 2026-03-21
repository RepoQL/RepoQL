using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Core;

/// <summary>
/// Hydrates <see cref="DocumentCatalog"/> from DuckDB on startup.
/// Queries existing documents to restore catalog state, enabling incremental indexing
/// across application restarts without re-processing unchanged files.
/// </summary>
public sealed class DuckDbDocumentCatalogDataSource : IDocumentCatalogDataSource
{
    private readonly DuckDbDataStore _store;
    private readonly ILogger<DuckDbDocumentCatalogDataSource> _logger;

    public DuckDbDocumentCatalogDataSource(
        DuckDbDataStore store,
        ILogger<DuckDbDocumentCatalogDataSource>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<DuckDbDocumentCatalogDataSource>.Instance;
    }

    public Task<IReadOnlyList<DocumentCatalogEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting catalog hydration from database...");

        try
        {
            const string sql = """
                SELECT n.uri, a.digest, a.media_type, n.id, n.updated_at
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document';
                """;

            var entries = _store.Read(sql, reader =>
            {
                var uriStr = reader.GetString(0);
                var rawDigest = reader.GetString(1);
                var mediaTypeStr = reader.GetString(2);
                // Column 3 is document_id (not used - catalog uses URI as key)
                var updatedAt = reader.GetDateTime(4);

                var digest = rawDigest;

                if (!RepoUri.TryParse(uriStr, out var uri))
                {
                    _logger.LogWarning("Skipping catalog entry with invalid URI: {Uri}", uriStr);
                    return null;
                }

                if (!SemanticMediaType.TryParse(mediaTypeStr, out var mediaType))
                {
                    _logger.LogWarning("Skipping catalog entry with invalid media type: {MediaType}", mediaTypeStr);
                    return null;
                }

                // PhysicalPath: derive from file:// URIs, null for others (embedded docs, imports)
                var physicalPath = uri.Scheme == "file" ? uri.Container.LocalPath : null;

                return new DocumentCatalogEntry(
                    uri,
                    digest,
                    mediaType,
                    physicalPath,
                    new DateTimeOffset(updatedAt, TimeSpan.Zero));
            }).Where(e => e is not null).Cast<DocumentCatalogEntry>().ToList();

            _logger.LogInformation("Loaded {Count} catalog entries from database", entries.Count);
            return Task.FromResult<IReadOnlyList<DocumentCatalogEntry>>(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load catalog from database; starting with empty catalog");
            return Task.FromResult<IReadOnlyList<DocumentCatalogEntry>>(Array.Empty<DocumentCatalogEntry>());
        }
    }
}
