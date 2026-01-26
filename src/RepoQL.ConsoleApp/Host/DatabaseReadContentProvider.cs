using System.Text.Json;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides read content backed by DuckDbDataStore for the gRPC host.
/// Complexity: Encapsulates SQL using matches_glob for unified URI pattern matching,
/// handling exact URIs, globs, and fragment patterns uniformly.
/// </summary>
internal sealed class DatabaseReadContentProvider(DuckDbDataStore db) : IReadContentProvider
{
    public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string uriPattern, CancellationToken cancellationToken)
    {
        var escapedPattern = uriPattern.Replace("'", "''", StringComparison.Ordinal);
        var hasFragment = uriPattern.Contains('#', StringComparison.Ordinal);

        // Unified query handles both document patterns and fragment patterns.
        // For documents: n.artifact_id is set, no span join needed.
        // For fragments: traverse span -> document -> artifact.
        // COALESCE picks the right artifact_id for both cases.
        var sql = $"""
            SELECT n.uri,
                   CASE WHEN s.start_line IS NOT NULL AND a.text_content IS NOT NULL
                        THEN array_to_string(
                            list_slice(string_split(a.text_content, chr(10)), s.start_line, s.end_line),
                            chr(10)
                        )
                        ELSE a.text_content
                   END as text_content,
                   a.media_type,
                   a.headline,
                   a.summary,
                   a.structure
            FROM node n
            LEFT JOIN span s ON s.id = n.span_id
            LEFT JOIN node doc ON doc.id = s.document_id
            JOIN artifact a ON a.id = COALESCE(n.artifact_id, doc.artifact_id)
            WHERE {(hasFragment ? "" : "n.kind = 'document' AND ")}
                  (matches_glob(n.uri, '{escapedPattern}', default_scheme := 'file:///')
                   OR matches_glob(n.uri, '{escapedPattern}', default_scheme := 'repoql-docs:///'))
            ORDER BY n.uri
            LIMIT 100
            """;

        var results = db.Query(sql);
        var documents = new List<ReadDocument>();

        foreach (var row in results)
        {
            documents.Add(new ReadDocument(
                Uri: row.TryGetValue("uri", out var u) ? u?.ToString() ?? "" : "",
                TextContent: row.TryGetValue("text_content", out var tc) ? tc?.ToString() : null,
                MediaType: row.TryGetValue("media_type", out var mt) ? mt?.ToString() : null,
                Headline: row.TryGetValue("headline", out var h) ? h?.ToString() : null,
                Summary: row.TryGetValue("summary", out var s) ? s?.ToString() : null,
                Structure: row.TryGetValue("structure", out var st) ? st?.ToString() : null));
        }

        return Task.FromResult<IReadOnlyList<ReadDocument>>(documents);
    }

    public Task<string?> GetRepoTreeAsync(string? scope, CancellationToken cancellationToken)
    {
        // Generate ASCII tree of repository structure using the tree() macro.
        // Limit to reasonable size to avoid bloating context.
        var sql = """
            SELECT tree(
                json_group_array(n.uri ORDER BY n.uri),
                json_group_array(a.headline ORDER BY n.uri),
                false
            ) as tree_output
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND n.uri LIKE 'file:///%'
            LIMIT 500
            """;

        try
        {
            var results = db.Query(sql);
            var row = results.FirstOrDefault();

            if (row is null || !row.TryGetValue("tree_output", out var treeOutput))
                return Task.FromResult<string?>(null);

            return Task.FromResult(treeOutput?.ToString());
        }
        catch
        {
            // Tree generation failed - not critical, return null.
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken)
    {
        if (uris.Count == 0)
            return Task.FromResult<string?>(null);

        string headlinesJson;
        if (includeHeadlines)
        {
            var headlines = LoadHeadlines(uris);
            var headlineList = uris.Select(uri => headlines.TryGetValue(uri, out var headline) ? headline : null).ToList();
            headlinesJson = JsonSerializer.Serialize(headlineList);
        }
        else
        {
            headlinesJson = "[]";
        }

        // Build JSON arrays for the tree() UDF.
        var urisJson = JsonSerializer.Serialize(uris);
        var escapedUrisJson = urisJson.Replace("'", "''", StringComparison.Ordinal);
        var escapedHeadlinesJson = headlinesJson.Replace("'", "''", StringComparison.Ordinal);

        var sql = $"SELECT tree('{escapedUrisJson}', '{escapedHeadlinesJson}', {(foldersOnly ? "true" : "false")}) as tree_output";

        try
        {
            var results = db.Query(sql);
            var row = results.FirstOrDefault();

            if (row is null || !row.TryGetValue("tree_output", out var treeOutput))
                return Task.FromResult<string?>(null);

            return Task.FromResult(treeOutput?.ToString());
        }
        catch
        {
            // Tree generation failed - return null, caller will fall back to list.
            return Task.FromResult<string?>(null);
        }
    }

    private Dictionary<string, string> LoadHeadlines(IReadOnlyList<string> uris)
    {
        var escapedUris = uris
            .Select(uri => $"'{uri.Replace("'", "''", StringComparison.Ordinal).ToLowerInvariant()}'")
            .ToList();

        if (escapedUris.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sql = $"""
            SELECT n.uri, a.headline
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE lower(n.uri) IN ({string.Join(", ", escapedUris)})
            """;

        var results = db.Query(sql);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in results)
        {
            var uri = row.TryGetValue("uri", out var u) ? u?.ToString() : null;
            var headline = row.TryGetValue("headline", out var h) ? h?.ToString() : null;
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(headline))
                continue;

            map[uri!] = headline!;
        }

        return map;
    }
}
