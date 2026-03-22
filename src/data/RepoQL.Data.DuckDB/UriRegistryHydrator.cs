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
            // Query all documents and their symbols, joining artifact for x-ray summaries.
            const string query = """
                SELECT
                    n.uri,
                    n.kind,
                    n.container_uri_lowercase,
                    a.headline,
                    a.structure
                FROM node n
                LEFT JOIN artifact a ON n.artifact_id = a.id
                WHERE n.kind = 'document' OR n.uri IS NOT NULL
                ORDER BY n.kind, n.uri
                """;

            var results = _db.ReadUntrusted(query, MapNodeRow);

            // Group symbols by their container (file)
            var symbolsByFile = new Dictionary<string, Dictionary<RepoUri, SymbolEntry>>(StringComparer.OrdinalIgnoreCase);
            var documents = new List<(RepoUri Uri, string? Headline, string? Structure)>();

            foreach (var (uri, kind, containerUri, headline, structure) in results)
            {
                if (uri is null)
                    continue;

                if (kind == "document")
                {
                    documents.Add((uri, headline, structure));
                }
                else if (uri is not null)
                {
                    // This is a symbol - add to its container's symbol list
                    // Prefer stored container URI for backwards compatibility, but derive from URI when absent.
                    var containerKey = !string.IsNullOrEmpty(containerUri)
                        ? RepoUri.NormalizeContainerKey(StripFragment(containerUri))
                        : RepoUri.NormalizeContainerKey(uri);

                    if (!symbolsByFile.TryGetValue(containerKey, out var symbols))
                    {
                        symbols = new Dictionary<RepoUri, SymbolEntry>();
                        symbolsByFile[containerKey] = symbols;
                    }

                    // Extract line range from symbol URI fragment for line-range operations
                    var (startLine, endLine) = ExtractLineRange(uri);
                    symbols[uri] = new SymbolEntry(kind ?? "unknown", startLine, endLine);
                    symbolCount++;
                }
            }

            // Create file entries
            foreach (var (docUri, headline, structure) in documents)
            {
                var containerKey = RepoUri.NormalizeContainerKey(docUri);
                var symbols = symbolsByFile.GetValueOrDefault(containerKey)
                    ?? new Dictionary<RepoUri, SymbolEntry>();

                var entry = new FileEntry(
                    Status: UriStatus.Indexed,
                    IndexedAt: DateTime.UtcNow,
                    Error: null,
                    EmbeddingStatus: EmbeddingStatus.NotApplicable, // Hydrated files completed pipeline; HydrateEmbeddings upgrades to Embedded
                    EmbeddedChunkCount: 0,
                    EmbeddedAt: null,
                    LineCount: 0, // Line count not available during hydration; will be populated during indexing
                    Symbols: symbols.AsReadOnly(),
                    Headline: headline,
                    Structure: structure);

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
                    repository_uri_container(uri) as container_uri,
                    COUNT(*) as chunk_count
                FROM document_embedding
                GROUP BY repository_uri_container(uri)
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

    private static (RepoUri? Uri, string? Kind, string? ContainerUri, string? Headline, string? Structure) MapNodeRow(IDataRecord record)
    {
        var uriStr = record["uri"]?.ToString();
        var kind = record["kind"]?.ToString();
        var containerUri = record["container_uri_lowercase"]?.ToString();
        var headline = record["headline"]?.ToString();
        var structure = record["structure"]?.ToString();

        RepoUri? uri = null;
        if (!string.IsNullOrEmpty(uriStr))
        {
            RepoUri.TryParse(uriStr, out uri);
        }

        return (uri, kind, containerUri, headline, structure);
    }

    private static (string? ContainerUri, int ChunkCount) MapEmbeddingRow(IDataRecord record)
    {
        var containerUri = record["container_uri"]?.ToString();
        var chunkCount = Convert.ToInt32(record["chunk_count"]);
        return (containerUri, chunkCount);
    }

    /// <summary>
    /// Strips the fragment (everything after #) from a URI string.
    /// </summary>
    private static string StripFragment(string uri)
    {
        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        return hashIndex >= 0 ? uri[..hashIndex] : uri;
    }

    /// <summary>
    /// Extracts line range from a URI's fragment (e.g., #line=20,21&amp;symbol=...).
    /// Returns (0, 0) if no line range is present.
    /// </summary>
    private static (int StartLine, int EndLine) ExtractLineRange(RepoUri uri)
    {
        var fragment = uri.Fragment;
        if (string.IsNullOrEmpty(fragment))
            return (0, 0);

        // Remove leading # if present
        if (fragment.StartsWith('#'))
            fragment = fragment[1..];

        // Look for line= parameter
        const string linePrefix = "line=";
        var lineIndex = fragment.IndexOf(linePrefix, StringComparison.OrdinalIgnoreCase);
        if (lineIndex < 0)
            return (0, 0);

        // Extract the value after "line="
        var valueStart = lineIndex + linePrefix.Length;
        var valueEnd = fragment.IndexOf('&', valueStart);
        var lineValue = valueEnd >= 0
            ? fragment[valueStart..valueEnd]
            : fragment[valueStart..];

        // Parse "start,end" or just "start"
        var parts = lineValue.Split(',');
        if (parts.Length == 1 && int.TryParse(parts[0], out var singleLine))
            return (singleLine, singleLine);

        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var start) &&
            int.TryParse(parts[1], out var end))
        {
            return (start, end);
        }

        return (0, 0);
    }
}
