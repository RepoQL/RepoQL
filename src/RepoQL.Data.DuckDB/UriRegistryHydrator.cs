using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Hydrates UriRegistry from the DuckDB database on startup.
///
/// Purpose: Rebuild the in-memory URI registry from persisted data when the
/// application starts, ensuring pattern matching and scope readiness work immediately.
///
/// Complexity: Queries node table for documents and symbols, groups by container,
/// and populates registry entries. Thread-safe via registry's ConcurrentDictionary.
/// </summary>
public class UriRegistryHydrator
{
    private readonly DuckDbDataStore _db;
    private readonly UriRegistry _registry;
    private readonly ILogger<UriRegistryHydrator> _logger;

    public UriRegistryHydrator(
        DuckDbDataStore db,
        UriRegistry registry,
        ILogger<UriRegistryHydrator>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? NullLogger<UriRegistryHydrator>.Instance;
    }

    /// <summary>
    /// Hydrates the registry from the database.
    /// Safe to call multiple times; will merge with existing entries.
    /// </summary>
    public void Hydrate()
    {
        _logger.LogInformation("Hydrating UriRegistry from database...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fileCount = 0;
        var symbolCount = 0;

        try
        {
            // Query all documents and their symbols
            const string query = """
                SELECT
                    n.uri,
                    n.kind,
                    n.container_uri_lowercase
                FROM node n
                WHERE n.kind = 'document' OR n.container_uri_lowercase IS NOT NULL
                ORDER BY n.container_uri_lowercase NULLS FIRST, n.uri
                """;

            var results = _db.ReadUntrusted(query, MapNodeRow);

            // Group symbols by their container (file)
            var symbolsByFile = new Dictionary<string, Dictionary<RepoUri, SymbolEntry>>(StringComparer.OrdinalIgnoreCase);
            var documents = new List<RepoUri>();

            foreach (var (uri, kind, containerUri) in results)
            {
                if (uri is null)
                    continue;

                if (kind == "document")
                {
                    documents.Add(uri);
                }
                else if (!string.IsNullOrEmpty(containerUri))
                {
                    // This is a symbol - add to its container's symbol list
                    // Note: Span data is not available during hydration; will be populated during indexing
                    if (!symbolsByFile.TryGetValue(containerUri, out var symbols))
                    {
                        symbols = new Dictionary<RepoUri, SymbolEntry>();
                        symbolsByFile[containerUri] = symbols;
                    }
                    symbols[uri] = SymbolEntry.WithKindOnly(kind ?? "unknown");
                    symbolCount++;
                }
            }

            // Create file entries
            foreach (var docUri in documents)
            {
                var containerKey = docUri.AbsoluteUri.ToLowerInvariant();
                var symbols = symbolsByFile.GetValueOrDefault(containerKey)
                    ?? new Dictionary<RepoUri, SymbolEntry>();

                var entry = new FileEntry(
                    Status: UriStatus.Indexed,
                    IndexedAt: DateTime.UtcNow,
                    Error: null,
                    EmbeddingStatus: EmbeddingStatus.Pending, // Will be updated by embedding hydration
                    EmbeddedChunkCount: 0,
                    EmbeddedAt: null,
                    LineCount: 0, // Line count not available during hydration; will be populated during indexing
                    Symbols: symbols.AsReadOnly());

                _registry[docUri] = entry;
                fileCount++;
            }

            sw.Stop();
            _logger.LogInformation(
                "UriRegistry hydrated: {FileCount} files, {SymbolCount} symbols in {ElapsedMs}ms",
                fileCount, symbolCount, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hydrate UriRegistry from database");
            throw;
        }
    }

    /// <summary>
    /// Updates embedding status for files based on the embedding table.
    /// </summary>
    public void HydrateEmbeddings()
    {
        _logger.LogDebug("Hydrating embedding status...");

        try
        {
            // Query embedding counts per container
            const string query = """
                SELECT
                    repoql_uri_container(uri) as container_uri,
                    COUNT(*) as chunk_count
                FROM embedding
                GROUP BY repoql_uri_container(uri)
                """;

            var results = _db.ReadUntrusted(query, MapEmbeddingRow);

            foreach (var (containerUriStr, chunkCount) in results)
            {
                if (string.IsNullOrEmpty(containerUriStr))
                    continue;

                if (!RepoUri.TryParse(containerUriStr, out var containerUri))
                    continue;

                if (_registry.TryGetValue(containerUri, out var existing))
                {
                    _registry[containerUri] = existing with
                    {
                        EmbeddingStatus = EmbeddingStatus.Embedded,
                        EmbeddedChunkCount = chunkCount,
                        EmbeddedAt = DateTime.UtcNow
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate embedding status (table may not exist yet)");
            // Non-fatal - embedding table might not exist yet
        }
    }

    private static (RepoUri? Uri, string? Kind, string? ContainerUri) MapNodeRow(IDataRecord record)
    {
        var uriStr = record["uri"]?.ToString();
        var kind = record["kind"]?.ToString();
        var containerUri = record["container_uri_lowercase"]?.ToString();

        RepoUri? uri = null;
        if (!string.IsNullOrEmpty(uriStr))
        {
            RepoUri.TryParse(uriStr, out uri);
        }

        return (uri, kind, containerUri);
    }

    private static (string? ContainerUri, int ChunkCount) MapEmbeddingRow(IDataRecord record)
    {
        var containerUri = record["container_uri"]?.ToString();
        var chunkCount = Convert.ToInt32(record["chunk_count"]);
        return (containerUri, chunkCount);
    }
}
