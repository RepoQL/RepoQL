using RepoQL.Data.DuckDB;
using RepoQL.Explore.Search;

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
        cancellationToken.ThrowIfCancellationRequested();

        var hasQuestion = !string.IsNullOrWhiteSpace(question);
        var hasScope = !string.IsNullOrWhiteSpace(scope);

        string sql;

        if (hasQuestion)
        {
            var escapedQuestion = EscapeSql(question!);

            // Use search for semantic + lexical search
            // If scope provided, pre-filter with glob_files (handles absolute path normalization)
            // then intersect with search results
            if (hasScope)
            {
                var escapedScope = EscapeSql(scope!);
                var escapedScopeLike = EscapeSql(ConvertScopeToSearchLike(scope!));
                sql = $"""
                    WITH scope_doc_ids AS (
                        SELECT DISTINCT d.id AS doc_id
                        FROM glob_files('{escapedScope}') sd
                        JOIN node d ON d.kind = 'document'
                            AND d.uri = split_part(sd.uri, '#', 1)
                    )
                    SELECT
                        hs.uri,
                        hs.headline,
                        hs.structure,
                        substr(COALESCE(
                            COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) || E'\n\n' || COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                            COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')),
                            COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                            ''), 1, 640) as snippet,
                        media_type_kind(a.media_type) as lang,
                        media_type_base(a.media_type) as semantic_type,
                        hs.sem_score,
                        hs.bm25_score,
                        hs.struct_mentions,
                        hs.body_mentions,
                        hs.score,
                        n.id as doc_id
                    FROM search('{escapedQuestion}', scope := '{escapedScopeLike}', k := {limit * 3}) hs
                    LEFT JOIN node n ON n.uri = hs.uri AND n.kind = 'document'
                    LEFT JOIN artifact a ON a.id = n.artifact_id
                    JOIN scope_doc_ids sd ON sd.doc_id = n.id
                    ORDER BY hs.score DESC
                    LIMIT {limit}
                    """;
            }
            else
            {
                sql = $"""
                    SELECT
                        hs.uri,
                        hs.headline,
                        hs.structure,
                        substr(COALESCE(
                            COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) || E'\n\n' || COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                            COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')),
                            COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                            ''), 1, 640) as snippet,
                        media_type_kind(a.media_type) as lang,
                        media_type_base(a.media_type) as semantic_type,
                        hs.sem_score,
                        hs.bm25_score,
                        hs.struct_mentions,
                        hs.body_mentions,
                        hs.score,
                        n.id as doc_id
                    FROM search('{escapedQuestion}', k := {limit * 3}) hs
                    LEFT JOIN node n ON n.uri = hs.uri AND n.kind = 'document'
                    LEFT JOIN artifact a ON a.id = n.artifact_id
                    ORDER BY hs.score DESC
                    LIMIT {limit}
                    """;
            }
        }
        else if (hasScope)
        {
            var escapedScope = EscapeSql(scope!);

            // Explore mode - scope only, no semantic search
            // Use glob_files for consistent scope semantics (including symbol/line fragment scopes)
            sql = $"""
                WITH scope_doc_ids AS (
                    SELECT DISTINCT d.id AS doc_id
                    FROM glob_files('{escapedScope}') sd
                    JOIN node d ON d.kind = 'document'
                        AND d.uri = split_part(sd.uri, '#', 1)
                )
                SELECT
                    n.uri,
                    COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) AS headline,
                    COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')) AS structure,
                    substr(COALESCE(
                        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) || E'\n\n' || COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')),
                        COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                        ''), 1, 640) as snippet,
                    media_type_kind(a.media_type) as lang,
                    media_type_base(a.media_type) as semantic_type,
                    0.0 as sem_score,
                    0.0 as bm25_score,
                    0 as struct_mentions,
                    0 as body_mentions,
                    0.5 as score,
                    n.id as doc_id
                FROM node n
                JOIN scope_doc_ids sd ON sd.doc_id = n.id
                LEFT JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document'
                ORDER BY
                    CASE
                        WHEN lower(n.uri) LIKE '%/node_modules/%'
                             OR lower(n.uri) LIKE '%/wwwroot/lib/%'
                             OR lower(n.uri) LIKE '%.map'
                             OR lower(n.uri) LIKE '%.min.js'
                             OR lower(n.uri) LIKE '%.min.css' THEN 4
                        WHEN lower(COALESCE(media_type_kind(a.media_type), '')) LIKE 'code.%' THEN 0
                        WHEN lower(COALESCE(media_type_kind(a.media_type), '')) LIKE 'query.%' THEN 1
                        WHEN lower(COALESCE(media_type_kind(a.media_type), '')) LIKE 'markdown.%'
                             OR lower(COALESCE(media_type_base(a.media_type), '')) = 'text/markdown' THEN 3
                        ELSE 2
                    END,
                    n.updated_at DESC,
                    n.uri
                LIMIT {limit}
                """;
        }
        else
        {
            // No question, no scope - show important/recent files
            sql = $"""
                SELECT
                    n.uri,
                    COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) AS headline,
                    COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')) AS structure,
                    substr(COALESCE(
                        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) || E'\n\n' || COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')),
                        COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
                        ''), 1, 640) as snippet,
                    media_type_kind(a.media_type) as lang,
                    media_type_base(a.media_type) as semantic_type,
                    0.0 as sem_score,
                    0.0 as bm25_score,
                    0 as struct_mentions,
                    0 as body_mentions,
                    0.5 as score,
                    n.id as doc_id
                FROM node n
                LEFT JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document'
                ORDER BY
                    CASE
                        WHEN n.uri LIKE 'help://%' THEN 0
                        WHEN n.uri LIKE '%README%' THEN 1
                        WHEN n.uri LIKE '%/docs/%' THEN 2
                        ELSE 3
                    END,
                    n.updated_at DESC
                LIMIT {limit}
                """;
        }

        var rows = _db.Query(sql, cancellationToken);

        var documents = new List<DocumentMatch>();
        var docIds = new List<string>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = row.GetValueOrDefault("uri")?.ToString();
            if (string.IsNullOrWhiteSpace(uri)) continue;

            documents.Add(new DocumentMatch(
                Uri: uri,
                Headline: row.GetValueOrDefault("headline")?.ToString(),
                Structure: row.GetValueOrDefault("structure")?.ToString(),
                Snippet: row.GetValueOrDefault("snippet")?.ToString(),
                Lang: row.GetValueOrDefault("lang")?.ToString(),
                SemanticType: row.GetValueOrDefault("semantic_type")?.ToString(),
                Score: Convert.ToDouble(row.GetValueOrDefault("score") ?? 0.5),
                SemanticScore: Convert.ToDouble(row.GetValueOrDefault("sem_score") ?? 0.0),
                NameHitScore: 0.0,
                RegexHitScore: (Convert.ToDouble(row.GetValueOrDefault("struct_mentions") ?? 0) +
                    Convert.ToDouble(row.GetValueOrDefault("body_mentions") ?? 0)) * 0.1,
                ChunkOverlapScore: Convert.ToDouble(row.GetValueOrDefault("bm25_score") ?? 0.0)
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
            chunkScores = GetChunkScores(docIds, cancellationToken);
        }

        return Task.FromResult(new DocumentSearchResult(documents, chunkScores));
    }

    /// <summary>
    /// Get chunk-level scores for proximity boosting.
    /// </summary>
    internal Dictionary<string, IReadOnlyList<ChunkScore>> GetChunkScores(IReadOnlyList<string> docIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (docIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<ChunkScore>>();

        var validDocIds = ParseValidGuids(docIds);
        if (validDocIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<ChunkScore>>();

        var docIdValues = string.Join(",\n                    ", validDocIds.Select(id => $"('{id:D}'::UUID)"));

        var sql = $"""
            WITH filter_doc_ids(doc_id) AS (
                SELECT doc_id
                FROM (VALUES
                    {docIdValues}
                ) AS ids(doc_id)
            )
            SELECT
                de.uri,
                de.chunk_index,
                s.start_line,
                s.end_line,
                1.0 as chunk_score
            FROM document_embedding de
            JOIN filter_doc_ids f ON f.doc_id = de.doc_id
            LEFT JOIN span s ON s.document_id = de.doc_id
                AND s.start_byte = de.start_byte
                AND s.end_byte = de.end_byte
            WHERE de.scope = 'document'
            ORDER BY de.uri, de.chunk_index
            """;

        try
        {
            var result = new Dictionary<string, List<ChunkScore>>();
            var rows = _db.Query(sql, cancellationToken);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // If chunk query fails, return empty - proximity boosting will be skipped
            return new Dictionary<string, IReadOnlyList<ChunkScore>>();
        }
    }

    private static List<Guid> ParseValidGuids(IReadOnlyList<string> ids)
    {
        var parsed = new List<Guid>(ids.Count);
        var seen = new HashSet<Guid>();

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (!Guid.TryParse(id, out var guid))
                continue;

            if (seen.Add(guid))
                parsed.Add(guid);
        }

        return parsed;
    }

    private static string ConvertScopeToSearchLike(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "%";

        // search(scope := ...) only supports a single SQL LIKE expression.
        // Fall back to wildcard when the glob syntax cannot be represented safely.
        if (scope.Contains(';', StringComparison.Ordinal) ||
            scope.Contains('!', StringComparison.Ordinal) ||
            scope.Contains('#', StringComparison.Ordinal))
        {
            return "%";
        }

        return scope
            .Replace("**", "%", StringComparison.Ordinal)
            .Replace("*", "%", StringComparison.Ordinal)
            .Replace("?", "_", StringComparison.Ordinal);
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");
}
