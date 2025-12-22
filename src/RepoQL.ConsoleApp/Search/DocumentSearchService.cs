using RepoQL.Data.DuckDB;
using RepoQL.Xray.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// Executes document-level search using the RepoQL database.
/// </summary>
internal sealed class DocumentSearchService : IDocumentSearchService
{
    private readonly DuckDbDataStore _db;

    public DocumentSearchService(DuckDbDataStore db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<DocumentSearchResult> SearchAsync(
        string? scope,
        string? question,
        int limit,
        CancellationToken cancellationToken)
    {
        var hasQuestion = !string.IsNullOrWhiteSpace(question);

        // Split scope by semicolons to support multiple patterns
        var scopePatterns = scope?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var hasScope = scopePatterns.Length > 0;

        string sql;

        if (hasQuestion)
        {
            var escapedQuestion = EscapeSql(question!);

            // Build scope parameter for hybrid_search (uses SQL LIKE pattern)
            var scopeParam = hasScope
                ? $", scope := '{EscapeSql(scopePatterns[0])}'"
                : "";

            // Use search for semantic + lexical search
            // search returns document-level results directly
            sql = $"""
                SELECT
                    hs.uri,
                    hs.headline,
                    hs.structure,
                    ri.body as snippet,
                    ri.lang,
                    ri.mime as semantic_type,
                    hs.score,
                    ri.doc_id
                FROM search('{escapedQuestion}'{scopeParam}, k := {limit * 3}) hs
                LEFT JOIN repo_index ri ON ri.uri = hs.uri AND ri.scope = 'document'
                ORDER BY hs.score DESC
                LIMIT {limit}
                """;
        }
        else if (hasScope)
        {
            // Build scope WHERE clause
            var scopeWhereClause = BuildScopeWhereClause("ri.uri", scopePatterns);

            // Explore mode - scope only, no semantic search
            sql = $"""
                SELECT
                    ri.uri,
                    ri.headline,
                    ri.structure,
                    ri.body as snippet,
                    ri.lang,
                    ri.mime as semantic_type,
                    0.5 as score,
                    ri.doc_id
                FROM repo_index ri
                WHERE ri.scope = 'document'
                  {scopeWhereClause.Replace("WHERE", "AND")}
                ORDER BY ri.mtime DESC, ri.uri
                LIMIT {limit}
                """;
        }
        else
        {
            // No question, no scope - show important/recent files
            sql = $"""
                SELECT
                    ri.uri,
                    ri.headline,
                    ri.structure,
                    ri.body as snippet,
                    ri.lang,
                    ri.mime as semantic_type,
                    0.5 as score,
                    ri.doc_id
                FROM repo_index ri
                WHERE ri.scope = 'document'
                ORDER BY
                    CASE
                        WHEN ri.uri LIKE 'docs://%' THEN 0
                        WHEN ri.uri LIKE '%README%' THEN 1
                        WHEN ri.uri LIKE '%/docs/%' THEN 2
                        ELSE 3
                    END,
                    ri.mtime DESC
                LIMIT {limit}
                """;
        }

        var rows = _db.Query(sql);

        var documents = new List<DocumentMatch>();
        var docIds = new List<string>();

        foreach (var row in rows)
        {
            var uri = row.GetValueOrDefault("uri")?.ToString();
            if (string.IsNullOrWhiteSpace(uri)) continue;

            documents.Add(new DocumentMatch(
                Uri: uri,
                Headline: row.GetValueOrDefault("headline")?.ToString(),
                Structure: row.GetValueOrDefault("structure")?.ToString(),
                Snippet: row.GetValueOrDefault("snippet")?.ToString(),
                Lang: row.GetValueOrDefault("lang")?.ToString(),
                SemanticType: row.GetValueOrDefault("semantic_type")?.ToString(),
                Score: Convert.ToDouble(row.GetValueOrDefault("score") ?? 0.5)
            ));

            // Collect doc_id for chunk query
            var docId = row.GetValueOrDefault("doc_id")?.ToString();
            if (!string.IsNullOrWhiteSpace(docId))
                docIds.Add(docId);
        }

        // Get chunk scores for proximity boosting (only if we have semantic search)
        var chunkScores = new Dictionary<string, IReadOnlyList<ChunkScore>>();
        if (hasQuestion && docIds.Count > 0)
        {
            chunkScores = GetChunkScores(docIds);
        }

        return Task.FromResult(new DocumentSearchResult(documents, chunkScores));
    }

    /// <summary>
    /// Get chunk-level scores for proximity boosting.
    /// </summary>
    private Dictionary<string, IReadOnlyList<ChunkScore>> GetChunkScores(IReadOnlyList<string> docIds)
    {
        if (docIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<ChunkScore>>();

        var docIdList = string.Join(",", docIds.Select(id => $"'{EscapeSql(id)}'"));

        var sql = $"""
            SELECT
                de.uri,
                de.chunk_index,
                s.start_line,
                s.end_line,
                1.0 as chunk_score
            FROM document_embedding de
            LEFT JOIN span s ON s.document_id = de.doc_id
                AND s.start_byte = de.start_byte
                AND s.end_byte = de.end_byte
            WHERE de.doc_id::text IN ({docIdList})
              AND de.scope = 'document'
            ORDER BY de.uri, de.chunk_index
            """;

        try
        {
            var result = new Dictionary<string, List<ChunkScore>>();
            var rows = _db.Query(sql);

            foreach (var row in rows)
            {
                var uri = row.GetValueOrDefault("uri")?.ToString();
                if (string.IsNullOrWhiteSpace(uri)) continue;

                var startLine = Convert.ToInt32(row.GetValueOrDefault("start_line") ?? 1);
                var endLine = Convert.ToInt32(row.GetValueOrDefault("end_line") ?? startLine + 50);
                var score = Convert.ToDouble(row.GetValueOrDefault("chunk_score") ?? 0.5);

                if (!result.TryGetValue(uri, out var chunks))
                {
                    chunks = [];
                    result[uri] = chunks;
                }

                chunks.Add(new ChunkScore(startLine, endLine, score));
            }

            return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ChunkScore>)kvp.Value);
        }
        catch
        {
            // If chunk query fails, return empty - proximity boosting will be skipped
            return new Dictionary<string, IReadOnlyList<ChunkScore>>();
        }
    }

    /// <summary>
    /// Build WHERE clause for scope patterns, supporting semicolon-delimited lists.
    /// </summary>
    private static string BuildScopeWhereClause(string columnName, string[] scopePatterns)
    {
        if (scopePatterns.Length == 0)
            return "";

        // Build OR conditions for each scope pattern, trying both file:/// and docs:/// schemes
        var conditions = new List<string>();
        foreach (var pattern in scopePatterns)
        {
            var escaped = EscapeSql(pattern);
            conditions.Add($"glob_match({columnName}, '{escaped}', default_scheme := 'file:///')");
            conditions.Add($"glob_match({columnName}, '{escaped}', default_scheme := 'docs:///')");
        }

        return $"WHERE ({string.Join(" OR ", conditions)})";
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");
}
