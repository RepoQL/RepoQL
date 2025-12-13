using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Protocol;
using RepoQL.Rendering.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// Executes document-level search using the RepoQL database.
/// </summary>
internal sealed class DocumentSearchService : IDocumentSearchService
{
    private readonly RepoQlClientProvider _clientProvider;

    public DocumentSearchService(RepoQlClientProvider clientProvider)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    }

    public async Task<DocumentSearchResult> SearchAsync(
        string? scope,
        string? question,
        int limit,
        CancellationToken cancellationToken)
    {
        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

        var hasQuestion = !string.IsNullOrWhiteSpace(question);

        // Split scope by semicolons to support multiple patterns
        var scopePatterns = scope?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var hasScope = scopePatterns.Length > 0;

        string sql;
        var parameters = new List<object?>();

        if (hasQuestion)
        {
            // IMPORTANT: Add file_search parameters FIRST (they appear first in the SQL)
            // Let semantic search do the heavy lifting - empty keywords
            // BM25 keywords were hurting results (tests matched keywords better than implementation)
            parameters.Add("");  // Empty keywords - rely on semantic search
            parameters.Add(question);

            // Build scope WHERE clause AFTER file_search params (WHERE clause appears after file_search in SQL)
            var scopeWhereClause = BuildScopeWhereClause("fs.uri", scopePatterns, parameters);

            // Semantic search with file_search macro
            // Also query chunk-level scores for proximity boosting
            sql = $"""
                WITH raw_results AS (
                    SELECT
                        split_part(fs.uri, '#', 1) as doc_uri,
                        fs.doc_id,
                        fs.score
                    FROM file_search(?, question := ?, k := {limit * 3}) fs
                    {scopeWhereClause}
                ),
                doc_results AS (
                    SELECT
                        doc_uri,
                        doc_id,
                        MAX(score) as score
                    FROM raw_results
                    GROUP BY doc_uri, doc_id
                    ORDER BY score DESC
                    LIMIT {limit}
                )
                SELECT
                    dr.doc_uri as uri,
                    ri.headline,
                    ri.structure,
                    ri.body as snippet,
                    ri.lang,
                    ri.mime as semantic_type,
                    dr.score,
                    dr.doc_id
                FROM doc_results dr
                LEFT JOIN repo_index ri ON ri.uri = dr.doc_uri AND ri.scope = 'document'
                ORDER BY dr.score DESC
                """;
        }
        else if (hasScope)
        {
            // Build scope WHERE clause
            var scopeWhereClause = BuildScopeWhereClause("ri.uri", scopePatterns, parameters);

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

        var response = await client.ExecuteRawQueryAsync(sql, parameters.ToArray(), null, cancellationToken).ConfigureAwait(false);

        var documents = new List<DocumentMatch>();
        var docIds = new List<string>();

        foreach (var row in response.Rows)
        {
            var values = row.Values;
            if (values.Count < 7) continue;

            var uri = ExtractString(values[0]);
            if (string.IsNullOrWhiteSpace(uri)) continue;

            documents.Add(new DocumentMatch(
                Uri: uri,
                Headline: ExtractString(values[1]),
                Structure: ExtractString(values[2]),
                Snippet: ExtractString(values[3]),
                Lang: ExtractString(values[4]),
                SemanticType: ExtractString(values[5]),
                Score: ExtractDouble(values[6]) ?? 0.5
            ));

            // Collect doc_id for chunk query
            if (values.Count > 7)
            {
                var docId = ExtractString(values[7]);
                if (!string.IsNullOrWhiteSpace(docId))
                    docIds.Add(docId);
            }
        }

        // Get chunk scores for proximity boosting (only if we have semantic search)
        var chunkScores = new Dictionary<string, IReadOnlyList<ChunkScore>>();
        if (hasQuestion && docIds.Count > 0)
        {
            chunkScores = await GetChunkScoresAsync(client, docIds, question!, cancellationToken).ConfigureAwait(false);
        }

        return new DocumentSearchResult(documents, chunkScores);
    }

    /// <summary>
    /// Get chunk-level scores for proximity boosting.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<ChunkScore>>> GetChunkScoresAsync(
        IRepoQlClient client,
        IReadOnlyList<string> docIds,
        string question,
        CancellationToken cancellationToken)
    {
        if (docIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<ChunkScore>>();

        // Query embeddings and compute similarity to question
        // For now, we'll use a simplified approach: get chunk boundaries and use document score as proxy
        // A more sophisticated implementation would compute per-chunk similarities

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
            var response = await client.ExecuteRawQueryAsync(sql, [], null, cancellationToken).ConfigureAwait(false);

            var result = new Dictionary<string, List<ChunkScore>>();

            foreach (var row in response.Rows)
            {
                var values = row.Values;
                if (values.Count < 4) continue;

                var uri = ExtractString(values[0]);
                if (string.IsNullOrWhiteSpace(uri)) continue;

                var startLine = ExtractInt(values[2]) ?? 1;
                var endLine = ExtractInt(values[3]) ?? startLine + 50;
                var score = ExtractDouble(values[4]) ?? 0.5;

                if (!result.TryGetValue(uri, out var chunks))
                {
                    chunks = new List<ChunkScore>();
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
    /// Extract meaningful keywords from a question for BM25 search.
    /// </summary>
    private static string ExtractKeywords(string question)
    {
        // Remove common question words and punctuation
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "how", "what", "where", "when", "why", "which", "who",
            "is", "are", "was", "were", "be", "been", "being",
            "do", "does", "did", "doing",
            "have", "has", "had", "having",
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "from", "as", "into", "through", "during",
            "can", "could", "would", "should", "will", "shall", "may", "might",
            "this", "that", "these", "those", "it", "its"
        };

        var words = question
            .Split(new[] { ' ', '\t', '\n', '\r', '?', '!', '.', ',', ';', ':', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Take(10);

        return string.Join(" ", words);
    }

    /// <summary>
    /// Build WHERE clause for scope patterns, supporting semicolon-delimited lists.
    /// </summary>
    private static string BuildScopeWhereClause(string columnName, string[] scopePatterns, List<object?> parameters)
    {
        if (scopePatterns.Length == 0)
            return "";

        // Build OR conditions for each scope pattern, trying both file:/// and docs:/// schemes
        var conditions = new List<string>();
        foreach (var pattern in scopePatterns)
        {
            conditions.Add($"glob_match({columnName}, ?, default_scheme := 'file:///')");
            parameters.Add(pattern);
            conditions.Add($"glob_match({columnName}, ?, default_scheme := 'docs:///')");
            parameters.Add(pattern);
        }

        return $"WHERE ({string.Join(" OR ", conditions)})";
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static string? ExtractString(Value value) =>
        value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };

    private static int? ExtractInt(Value value)
    {
        var str = ExtractString(value);
        return !string.IsNullOrWhiteSpace(str) && int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ExtractDouble(Value value)
    {
        if (value.KindCase == Value.KindOneofCase.NumberValue)
            return value.NumberValue;

        var str = ExtractString(value);
        return !string.IsNullOrWhiteSpace(str) && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
