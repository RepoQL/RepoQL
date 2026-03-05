using RepoQL.Data.DuckDB;
using RepoQL.Explore.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// Executes object-level search within specific documents.
/// </summary>
internal sealed class ObjectSearchService : IObjectSearchService
{
    private readonly DuckDbDataStore _db;

    public ObjectSearchService(DuckDbDataStore db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<IReadOnlyList<ObjectMatch>> SearchInDocumentsAsync(
        IReadOnlyList<string> documentUris,
        string? question,
        int objectsPerDocument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (documentUris.Count == 0)
            return Task.FromResult<IReadOnlyList<ObjectMatch>>([]);

        var normalizedDocumentUris = documentUris
            .Select(NormalizeDocumentUri)
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedDocumentUris.Count == 0)
            return Task.FromResult<IReadOnlyList<ObjectMatch>>([]);

        var hasQuestion = !string.IsNullOrWhiteSpace(question);
        var results = new List<ObjectMatch>();
        var objectCountByDoc = new Dictionary<string, int>();

        // For strong matches with a question, do embedding search
        if (hasQuestion)
        {
            var allObjects = SearchObjectsInDocuments(normalizedDocumentUris, question!, objectsPerDocument, cancellationToken);

            foreach (var obj in allObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(obj);
                objectCountByDoc.TryGetValue(obj.DocumentUri, out var count);
                objectCountByDoc[obj.DocumentUri] = count + 1;
            }
        }

        // For documents where we need more objects (embedding found none or fewer than requested)
        var docsNeedingMore = normalizedDocumentUris
            .Where(u => !objectCountByDoc.TryGetValue(u, out var count) || count < objectsPerDocument)
            .ToList();

        if (docsNeedingMore.Count > 0)
        {
            var remainingPerDoc = docsNeedingMore.ToDictionary(
                u => u,
                u => objectCountByDoc.TryGetValue(u, out var count) ? objectsPerDocument - count : objectsPerDocument);

            var fallbackObjects = GetObjectsByPosition(docsNeedingMore, objectsPerDocument, cancellationToken);

            var existingUris = results.Select(r => r.Uri).ToHashSet();
            foreach (var obj in fallbackObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingUris.Contains(obj.Uri))
                    continue;

                if (remainingPerDoc.TryGetValue(obj.DocumentUri, out var remaining) && remaining > 0)
                {
                    results.Add(obj);
                    remainingPerDoc[obj.DocumentUri] = remaining - 1;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ObjectMatch>>(results);
    }

    private List<ObjectMatch> SearchObjectsInDocuments(
        IReadOnlyList<string> documentUris,
        string question,
        int objectsPerDocument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (documentUris.Count == 0)
            return [];

        var escapedQuestion = EscapeSql(question);
        var escapedUriGlob = EscapeSql(BuildObjectUriGlob(documentUris));
        var totalLimit = documentUris.Count * objectsPerDocument * 2;

        var sql = $"""
            WITH search_results AS (
                SELECT
                    s.uri,
                    s.symbol,
                    s.kind,
                    s.headline,
                    s.structure,
                    s.snippet,
                    s.line_start,
                    s.line_end,
                    s.lang,
                    s.mime as semantic_type,
                    s.score,
                    s.doc_semn as semantic_score,
                    s.fuzzy_score as name_hit_score,
                    s.bm25_score as chunk_overlap_score,
                    split_part(s.uri, '#', 1) as document_uri
                FROM _search_candidates(
                    '{escapedQuestion}',
                    k := {Math.Max(50, totalLimit)},
                    uri_glob := '{escapedUriGlob}'
                ) s
                WHERE s.node_scope = 'object'
            ),
            ranked AS (
                SELECT *,
                    ROW_NUMBER() OVER (PARTITION BY document_uri ORDER BY score DESC) as rn
                FROM search_results
            )
            SELECT
                uri, symbol, kind, headline, structure, snippet, line_start, line_end, lang, semantic_type, score,
                semantic_score, name_hit_score, chunk_overlap_score, document_uri
            FROM ranked
            WHERE rn <= {objectsPerDocument}
            ORDER BY score DESC
            """;

        try
        {
            var results = new List<ObjectMatch>();
            var rows = _db.Query(sql, cancellationToken);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = row.GetValueOrDefault("uri")?.ToString();
                var kind = row.GetValueOrDefault("kind")?.ToString();
                var docUri = row.GetValueOrDefault("document_uri")?.ToString();

                if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(docUri))
                    continue;

                results.Add(new ObjectMatch(
                    Uri: uri,
                    DocumentUri: docUri,
                    Kind: kind,
                    Symbol: row.GetValueOrDefault("symbol")?.ToString(),
                    Headline: row.GetValueOrDefault("headline")?.ToString(),
                    Structure: row.GetValueOrDefault("structure")?.ToString(),
                    Snippet: row.GetValueOrDefault("snippet")?.ToString(),
                    LineStart: Convert.ToInt32(row.GetValueOrDefault("line_start") ?? 1),
                    LineEnd: Convert.ToInt32(row.GetValueOrDefault("line_end") ?? 1),
                    Lang: row.GetValueOrDefault("lang")?.ToString(),
                    SemanticType: row.GetValueOrDefault("semantic_type")?.ToString(),
                    Score: Convert.ToDouble(row.GetValueOrDefault("score") ?? 0.5),
                    SemanticScore: Convert.ToDouble(row.GetValueOrDefault("semantic_score") ?? 0.0),
                    NameHitScore: Convert.ToDouble(row.GetValueOrDefault("name_hit_score") ?? 0.0),
                    RegexHitScore: 0.0,
                    ChunkOverlapScore: Convert.ToDouble(row.GetValueOrDefault("chunk_overlap_score") ?? 0.0)
                ));
            }

            return results;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private List<ObjectMatch> GetObjectsByPosition(
        IReadOnlyList<string> documentUris,
        int objectsPerDocument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (documentUris.Count == 0)
            return [];

        var uriValues = string.Join(",\n                    ", documentUris.Select(u => $"('{EscapeSql(u)}')"));

        var sql = $"""
            WITH input_docs(uri) AS (
                SELECT uri
                FROM (VALUES
                    {uriValues}
                ) AS input(uri)
            ),
            target_docs AS (
                SELECT DISTINCT
                    d.id AS doc_id,
                    d.uri AS document_uri
                FROM input_docs i
                JOIN node d ON d.kind = 'document'
                    AND d.uri = split_part(i.uri, '#', 1)
            ),
            objects AS (
                SELECT
                    COALESCE(
                        child.uri,
                        repository_uri_join(
                            doc.uri,
                            COALESCE(
                                fragment_from_line_range(CAST(sp.start_line AS VARCHAR), CAST(sp.end_line AS VARCHAR)),
                                concat('node/', child.kind, '/', REPLACE(CAST(child.id AS VARCHAR), '-', ''))
                            )
                        )
                    ) AS uri,
                    td.document_uri,
                    child.kind,
                    COALESCE(
                        repository_uri_symbol(child.uri),
                        json_extract_string(child.properties, '$.symbol'),
                        json_extract_string(child.properties, '$.name')
                    ) AS symbol,
                    COALESCE(
                        NULLIF(child.headline, ''),
                        json_extract_string(child.properties, '$.name'),
                        repository_uri_file_name(doc.uri)
                    ) AS headline,
                    NULLIF(child.structure, '') AS structure,
                    substr(COALESCE(
                        COALESCE(NULLIF(child.headline, ''), json_extract_string(child.properties, '$.name'), repository_uri_file_name(doc.uri))
                            || E'\n\n' || NULLIF(child.structure, ''),
                        COALESCE(NULLIF(child.headline, ''), json_extract_string(child.properties, '$.name'), repository_uri_file_name(doc.uri)),
                        NULLIF(child.structure, ''),
                        ''), 1, 640) as snippet,
                    COALESCE(sp.start_line, TRY_CAST(repository_uri_line_start(child.uri) AS INTEGER)) AS line_start,
                    COALESCE(sp.end_line, TRY_CAST(repository_uri_line_end(child.uri) AS INTEGER)) AS line_end,
                    media_type_kind(a.media_type) as lang,
                    media_type_base(a.media_type) as semantic_type,
                    0.5 as score,
                    0.0 as semantic_score,
                    0.0 as name_hit_score,
                    0.0 as chunk_overlap_score,
                    ROW_NUMBER() OVER (
                        PARTITION BY td.document_uri
                        ORDER BY sp.start_line NULLS LAST
                    ) as rn
                FROM node child
                JOIN span sp ON sp.id = child.span_id
                JOIN node doc ON doc.id = sp.document_id
                JOIN target_docs td ON td.doc_id = doc.id
                LEFT JOIN artifact a ON a.id = doc.artifact_id
                WHERE child.kind <> 'document'
            )
            SELECT
                uri, document_uri, kind, symbol, headline, structure, snippet,
                line_start, line_end, lang, semantic_type, score,
                semantic_score, name_hit_score, chunk_overlap_score
            FROM objects
            WHERE rn <= {objectsPerDocument}
            ORDER BY document_uri, line_start
            """;

        var results = new List<ObjectMatch>();
        var rows = _db.Query(sql, cancellationToken);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = row.GetValueOrDefault("uri")?.ToString();
            var docUri = row.GetValueOrDefault("document_uri")?.ToString();
            var kind = row.GetValueOrDefault("kind")?.ToString();

            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(docUri) || string.IsNullOrWhiteSpace(kind))
                continue;

            results.Add(new ObjectMatch(
                Uri: uri,
                DocumentUri: docUri,
                Kind: kind,
                Symbol: row.GetValueOrDefault("symbol")?.ToString(),
                Headline: row.GetValueOrDefault("headline")?.ToString(),
                Structure: row.GetValueOrDefault("structure")?.ToString(),
                Snippet: row.GetValueOrDefault("snippet")?.ToString(),
                LineStart: Convert.ToInt32(row.GetValueOrDefault("line_start") ?? 1),
                LineEnd: Convert.ToInt32(row.GetValueOrDefault("line_end") ?? 1),
                Lang: row.GetValueOrDefault("lang")?.ToString(),
                SemanticType: row.GetValueOrDefault("semantic_type")?.ToString(),
                Score: Convert.ToDouble(row.GetValueOrDefault("score") ?? 0.5),
                SemanticScore: Convert.ToDouble(row.GetValueOrDefault("semantic_score") ?? 0.0),
                NameHitScore: Convert.ToDouble(row.GetValueOrDefault("name_hit_score") ?? 0.0),
                RegexHitScore: 0.0,
                ChunkOverlapScore: Convert.ToDouble(row.GetValueOrDefault("chunk_overlap_score") ?? 0.0)
            ));
        }

        return results;
    }

    private static string BuildObjectUriGlob(IEnumerable<string> documentUris)
        => string.Join(";", documentUris.Select(uri => $"{uri}#*"));

    private static string NormalizeDocumentUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return string.Empty;

        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        return hashIndex >= 0 ? uri[..hashIndex] : uri;
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");
}
