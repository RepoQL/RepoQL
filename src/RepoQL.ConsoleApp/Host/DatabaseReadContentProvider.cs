using System.Text.Json;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;
using RepoQL.Read;

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

        // Unified query handles both document patterns and fragment patterns.
        // For documents: n.artifact_id is set, no span join needed.
        // For fragments: traverse span -> document -> artifact.
        // COALESCE picks the right artifact_id for both cases.
        // Use glob_files() table function for proper symbol pattern matching.
        var sql = $"""
            SELECT g.uri,
                   CASE
                        WHEN COALESCE(
                                 TRY_CAST(repository_uri_line_start(g.uri) AS INTEGER),
                                 s.start_line
                             ) IS NOT NULL
                             AND a.text_content IS NOT NULL
                        THEN array_to_string(
                            list_slice(
                                string_split(a.text_content, chr(10)),
                                COALESCE(
                                    TRY_CAST(repository_uri_line_start(g.uri) AS INTEGER),
                                    s.start_line
                                ),
                                COALESCE(
                                    TRY_CAST(repository_uri_line_end(g.uri) AS INTEGER),
                                    TRY_CAST(repository_uri_line_start(g.uri) AS INTEGER),
                                    s.end_line,
                                    s.start_line
                                )
                            ),
                            chr(10)
                        )
                        ELSE a.text_content
                   END as text_content,
                   a.media_type,
                   a.headline,
                   a.summary,
                   a.structure
            FROM glob_files('{escapedPattern}') g
            LEFT JOIN node n ON n.uri = g.uri
            LEFT JOIN span s ON s.id = n.span_id
            LEFT JOIN node doc_by_span ON doc_by_span.id = s.document_id
            LEFT JOIN node doc_by_container
                ON doc_by_container.kind = 'document'
               AND doc_by_container.container_uri_lowercase = lower(repository_uri_container(g.uri))
            JOIN artifact a ON a.id = COALESCE(n.artifact_id, doc_by_span.artifact_id, doc_by_container.artifact_id)
            {(hasFragment ? "" : "WHERE COALESCE(n.kind, doc_by_container.kind) = 'document'")}
            ORDER BY g.uri
            """;

        return Task.FromResult(QueryReadDocuments(sql, cancellationToken));
    }

    private IReadOnlyList<ReadDocument> QueryReadDocuments(string sql, CancellationToken cancellationToken)
    {
        var results = db.Query(sql, cancellationToken);
        var documents = new List<ReadDocument>();

        foreach (var row in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    public async Task<string?> GetRepoTreeAsync(string? scope, int tokenBudget, CancellationToken cancellationToken)
    {
        try
        {
            // Fetch all document URIs (lightweight — no tree rendering yet).
            var sql = "SELECT n.uri FROM node n WHERE n.kind = 'document' ORDER BY n.uri";
            var results = db.Query(sql, cancellationToken);
            var uris = results.Select(r => r["uri"]?.ToString()!).Where(u => u is not null).ToList();

            if (uris.Count == 0)
                return null;

            // Progressive fallback: headlines → files → folders → null.
            var fit = await TreeHandler.FitToBudgetAsync(
                this, uris, tokenBudget, TreeHandler.TreeDetailLevel.Headlines, cancellationToken)
                .ConfigureAwait(false);

            return fit?.Content;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Tree generation failed - not critical, return null.
            return null;
        }
    }

    public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken)
    {
        if (uris.Count == 0)
            return Task.FromResult<string?>(null);

        string headlinesJson;
        if (includeHeadlines)
        {
            var headlines = LoadHeadlines(uris, cancellationToken);
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
            var results = db.Query(sql, cancellationToken);
            var row = results.FirstOrDefault();

            if (row is null || !row.TryGetValue("tree_output", out var treeOutput))
                return Task.FromResult<string?>(null);

            return Task.FromResult(treeOutput?.ToString());
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Tree generation failed - return null, caller will fall back to list.
            return Task.FromResult<string?>(null);
        }
    }

    private Dictionary<string, string> LoadHeadlines(IReadOnlyList<string> uris, CancellationToken cancellationToken)
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

        var results = db.Query(sql, cancellationToken);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = row.TryGetValue("uri", out var u) ? u?.ToString() : null;
            var headline = row.TryGetValue("headline", out var h) ? h?.ToString() : null;
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(headline))
                continue;

            map[uri!] = headline!;
        }

        return map;
    }
}
