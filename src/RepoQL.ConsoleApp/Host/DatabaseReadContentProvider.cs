using System.Text.Json;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides read content backed by DuckDbDataStore for the gRPC host.
/// Complexity: Encapsulates SQL for glob/fragment resolution and line extraction so the
/// read orchestration remains storage-agnostic.
/// </summary>
internal sealed class DatabaseReadContentProvider(DuckDbDataStore db) : IReadContentProvider
{
    public Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        var escapedUri = uri.Replace("'", "''", StringComparison.Ordinal);
        var hasFragment = uri.Contains('#', StringComparison.Ordinal);

        // For fragment URIs (symbols, line ranges), join through span -> document -> artifact
        // since sub-document nodes have NULL artifact_id.
        var sql = hasFragment ? $"""
                                 SELECT n.uri,
                                        -- Extract lines from full content based on span
                                        CASE WHEN s.start_line IS NOT NULL AND a.text_content IS NOT NULL
                                             THEN (SELECT string_agg(line, chr(10))
                                                   FROM (SELECT unnest(string_split(a.text_content, chr(10))) as line,
                                                                generate_subscripts(string_split(a.text_content, chr(10)), 1) as line_num)
                                                   WHERE line_num >= s.start_line AND line_num <= s.end_line)
                                             ELSE a.text_content
                                        END as text_content,
                                        a.media_type,
                                        a.headline,
                                        a.summary,
                                        a.structure
                                 FROM node n
                                 JOIN span s ON s.id = n.span_id
                                 JOIN node d ON d.id = s.document_id
                                 JOIN artifact a ON a.id = d.artifact_id
                                 WHERE lower(n.uri) = lower('{escapedUri}')
                                 LIMIT 1
                                 """ 
                                    : $"""
                                        SELECT n.uri,
                                               a.text_content,
                                               a.media_type,
                                               a.headline,
                                               a.summary,
                                               a.structure
                                        FROM node n
                                        JOIN artifact a ON a.id = n.artifact_id
                                        WHERE lower(n.uri) = lower('{escapedUri}')
                                        LIMIT 1
                                        """;

        var results = db.Query(sql);
        var row = results.FirstOrDefault();

        if (row is null)
            return Task.FromResult<ReadDocument?>(null);

        return Task.FromResult<ReadDocument?>(new ReadDocument(
            Uri: row.TryGetValue("uri", out var u) ? u?.ToString() ?? uri : uri,
            TextContent: row.TryGetValue("text_content", out var tc) ? tc?.ToString() : null,
            MediaType: row.TryGetValue("media_type", out var mt) ? mt?.ToString() : null,
            Headline: row.TryGetValue("headline", out var h) ? h?.ToString() : null,
            Summary: row.TryGetValue("summary", out var s) ? s?.ToString() : null,
            Structure: row.TryGetValue("structure", out var st) ? st?.ToString() : null));
    }

    public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken)
    {
        var escapedGlob = globUri.Replace("'", "''", StringComparison.Ordinal);
        var hasFragment = globUri.Contains('#', StringComparison.Ordinal);

        // Use matches_glob which supports:
        // - Semicolon-delimited patterns (src/**;lib/**)
        // - Exclusion patterns (!tests/**)
        // - Fragment patterns (#symbol=MyClass.*)
        string sql;
        if (hasFragment)
        {
            // For fragment patterns, nodes don't have their own artifact.
            // Join through span -> document -> artifact to get content.
            // Extract just the lines for this symbol/range from the full text.
            sql = $"""
                SELECT n.uri,
                       -- Extract lines from full content based on span
                       CASE WHEN s.start_line IS NOT NULL AND a.text_content IS NOT NULL
                            THEN (SELECT string_agg(line, chr(10))
                                  FROM (SELECT unnest(string_split(a.text_content, chr(10))) as line,
                                               generate_subscripts(string_split(a.text_content, chr(10)), 1) as line_num)
                                  WHERE line_num >= s.start_line AND line_num <= s.end_line)
                            ELSE a.text_content
                       END as text_content,
                       a.media_type,
                       a.headline,
                       a.summary,
                       a.structure
                FROM node n
                JOIN span s ON s.id = n.span_id
                JOIN node d ON d.id = s.document_id
                JOIN artifact a ON a.id = d.artifact_id
                WHERE (matches_glob(n.uri, '{escapedGlob}', default_scheme := 'file:///')
                       OR matches_glob(n.uri, '{escapedGlob}', default_scheme := 'repoql-docs:///'))
                ORDER BY n.uri
                LIMIT 100
                """;
        }
        else
        {
            // For document patterns, use simple join.
            sql = $"""
                SELECT n.uri,
                       a.text_content,
                       a.media_type,
                       a.headline,
                       a.summary,
                       a.structure
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document'
                  AND (matches_glob(n.uri, '{escapedGlob}', default_scheme := 'file:///')
                       OR matches_glob(n.uri, '{escapedGlob}', default_scheme := 'repoql-docs:///'))
                ORDER BY n.uri
                LIMIT 100
                """;
        }

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
            SELECT tree(list(uri ORDER BY uri)) as tree_output
            FROM node
            WHERE kind = 'document'
              AND uri LIKE 'file:///%'
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

    public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, CancellationToken cancellationToken)
    {
        if (uris.Count == 0)
            return Task.FromResult<string?>(null);

        // Build JSON array of URIs for the tree() UDF.
        var jsonArray = JsonSerializer.Serialize(uris);
        var escapedJson = jsonArray.Replace("'", "''", StringComparison.Ordinal);

        var sql = $"SELECT tree('{escapedJson}', {(foldersOnly ? "true" : "false")}) as tree_output";

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
}
