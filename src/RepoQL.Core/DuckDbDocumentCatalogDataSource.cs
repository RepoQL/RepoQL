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
    private readonly IDuckDBConnectionFactory _connectionFactory;
    private readonly ILogger<DuckDbDocumentCatalogDataSource> _logger;

    public DuckDbDocumentCatalogDataSource(
        IDuckDBConnectionFactory connectionFactory,
        ILogger<DuckDbDocumentCatalogDataSource>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? NullLogger<DuckDbDocumentCatalogDataSource>.Instance;
    }

    public async Task<IReadOnlyList<DocumentCatalogEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        var entries = new List<DocumentCatalogEntry>();
        _logger.LogInformation("Starting catalog hydration from database...");

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            _logger.LogDebug("Database connection opened for catalog hydration");
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT n.uri, a.digest, a.media_type, n.id, n.updated_at
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document';";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var uriStr = reader.GetString(0);
                var rawDigest = reader.GetString(1);
                var mediaTypeStr = reader.GetString(2);

                // Database stores "xxh64:abc123", but catalog compares against "ABC123" (hex only, uppercase)
                var digest = rawDigest.Contains(':')
                    ? rawDigest[(rawDigest.IndexOf(':') + 1)..].ToUpperInvariant()
                    : rawDigest.ToUpperInvariant();
                // Column 3 is document_id (not used - catalog uses URI as key)
                var updatedAt = reader.GetDateTime(4);

                if (!RepoUri.TryParse(uriStr, out var uri))
                {
                    _logger.LogWarning("Skipping catalog entry with invalid URI: {Uri}", uriStr);
                    continue;
                }

                if (!SemanticMediaType.TryParse(mediaTypeStr, out var mediaType))
                {
                    _logger.LogWarning("Skipping catalog entry with invalid media type: {MediaType}", mediaTypeStr);
                    continue;
                }

                // PhysicalPath: derive from file:// URIs, null for others (embedded docs, imports)
                var physicalPath = uri.Scheme == "file" ? uri.Container.LocalPath : null;

                entries.Add(new DocumentCatalogEntry(
                    uri,
                    digest,
                    mediaType,
                    physicalPath,
                    new DateTimeOffset(updatedAt, TimeSpan.Zero)));
            }

            _logger.LogInformation("Loaded {Count} catalog entries from database", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load catalog from database; starting with empty catalog");
            return Array.Empty<DocumentCatalogEntry>();
        }

        return entries;
    }
}
