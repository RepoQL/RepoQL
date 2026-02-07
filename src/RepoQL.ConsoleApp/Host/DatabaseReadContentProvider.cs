using System.Text.Json;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides read content backed by DuckDbDataStore for the gRPC host.
/// Complexity: Encapsulates SQL using glob_files() table function for unified URI pattern matching,
/// handling exact URIs, globs, and fragment patterns (including symbol wildcards) uniformly.
/// </summary>
internal sealed class DatabaseReadContentProvider(DuckDbDataStore db) : IReadContentProvider
{
    public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string uriPattern, CancellationToken cancellationToken)
    {
        var escapedPattern = uriPattern.Replace("'", "''", StringComparison.Ordinal);
        var hasFragment = uriPattern.Contains('#', StringComparison.Ordinal);
        var isPlainAnchorPattern = IsPlainAnchorPattern(uriPattern);

        // Fallback for markdown-style anchor fragments (#slug). This path does not depend on
        // heading node URIs being present in the registry, so legacy indexed markdown still works.
        if (hasFragment && isPlainAnchorPattern)
        {
            var anchorSql = $"""
                WITH heading_matches AS (
                    SELECT
                        repository_uri_join(d.uri, json_extract_string(h.properties, '$.slug')) AS heading_uri,
                        hs.start_line,
                        hs.end_line,
                        a.text_content,
                        a.media_type,
                        a.headline,
                        a.summary,
                        a.structure
                    FROM node d
                    JOIN artifact a ON a.id = d.artifact_id
                    JOIN edge e ON e.source_node_id = d.id
                              AND e.type = 'HAS_PART'
                              AND e.is_composition = TRUE
                    JOIN node h ON h.id = e.destination_node_id
                              AND h.kind = 'md_heading'
                    LEFT JOIN span hs ON hs.id = h.span_id
                    WHERE d.kind = 'document'
                      AND json_extract_string(h.properties, '$.slug') IS NOT NULL
                      AND json_extract_string(h.properties, '$.slug') <> ''
                      AND matches_glob(
                          repository_uri_join(d.uri, json_extract_string(h.properties, '$.slug')),
                          '{escapedPattern}')
                )
                SELECT
                    heading_uri AS uri,
                    CASE WHEN start_line IS NOT NULL AND text_content IS NOT NULL
                         THEN array_to_string(
                             list_slice(string_split(text_content, chr(10)), start_line, end_line),
                             chr(10)
                         )
                         ELSE text_content
                    END as text_content,
                    media_type,
                    headline,
                    summary,
                    structure
                FROM heading_matches
                ORDER BY heading_uri
                LIMIT 100
                """;

            var anchorDocuments = QueryReadDocuments(anchorSql);
            if (anchorDocuments.Count > 0)
                return Task.FromResult(anchorDocuments);
        }

        // Unified query handles both document patterns and fragment patterns.
        // For documents: n.artifact_id is set, no span join needed.
        // For fragments: traverse span -> document -> artifact.
        // COALESCE picks the right artifact_id for both cases.
        // Use glob_files() table function for proper symbol pattern matching.
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
            FROM glob_files('{escapedPattern}') g
            JOIN node n ON n.uri = g.uri
            LEFT JOIN span s ON s.id = n.span_id
            LEFT JOIN node doc ON doc.id = s.document_id
            JOIN artifact a ON a.id = COALESCE(n.artifact_id, doc.artifact_id)
            {(hasFragment ? "" : "WHERE n.kind = 'document'")}
            ORDER BY n.uri
            """;

        return Task.FromResult(QueryReadDocuments(sql));
    }

    private IReadOnlyList<ReadDocument> QueryReadDocuments(string sql)
    {
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

        return documents;
    }

    private static bool IsPlainAnchorPattern(string uriPattern)
    {
        if (string.IsNullOrWhiteSpace(uriPattern))
            return false;

        if (uriPattern.Contains(';', StringComparison.Ordinal))
            return false;

        var hashIndex = uriPattern.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex < 0 || hashIndex == uriPattern.Length - 1)
            return false;

        var fragment = uriPattern[(hashIndex + 1)..];
        return !fragment.Contains('=', StringComparison.Ordinal);
    }

    public Task<string?> GetRepoTreeAsync(string? scope, CancellationToken cancellationToken)
    {
        // Generate ASCII tree of repository structure using the tree() macro.
        // Include headlines for full context - the tree with headlines typically fits
        // within ~20k tokens even for large repos, and gives the LLM useful file summaries.
        var sql = """
            SELECT tree(
                json_group_array(n.uri ORDER BY n.uri),
                json_group_array(a.headline ORDER BY n.uri),
                false
            ) as tree_output
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
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
