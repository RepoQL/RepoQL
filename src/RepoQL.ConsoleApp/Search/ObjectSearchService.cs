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
            var allObjects = SearchObjectsInDocuments(normalizedDocumentUris, question!, objectsPerDocument);

            foreach (var obj in allObjects)
            {
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

            var fallbackObjects = GetObjectsByPosition(docsNeedingMore, objectsPerDocument);

            var existingUris = results.Select(r => r.Uri).ToHashSet();
            foreach (var obj in fallbackObjects)
            {
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
        int objectsPerDocument)
    {
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
                    split_part(s.uri, '#', 1) as document_uri
                FROM _search_candidates(
                    '{escapedQuestion}',
                    k := {Math.Max(50, totalLimit)},
                    uri_glob := '{escapedUriGlob}'
                ) s
                WHERE s.scope = 'object'
            ),
            ranked AS (
                SELECT *,
                    ROW_NUMBER() OVER (PARTITION BY document_uri ORDER BY score DESC) as rn
                FROM search_results
            )
            SELECT uri, symbol, kind, headline, structure, snippet, line_start, line_end, lang, semantic_type, score, document_uri
            FROM ranked
            WHERE rn <= {objectsPerDocument}
            ORDER BY score DESC
            """;

        try
        {
            var results = new List<ObjectMatch>();
            var rows = _db.Query(sql);

            foreach (var row in rows)
            {
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
                    Score: Convert.ToDouble(row.GetValueOrDefault("score") ?? 0.5)
                ));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private List<ObjectMatch> GetObjectsByPosition(
        IReadOnlyList<string> documentUris,
        int objectsPerDocument)
    {
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
                    ri.uri,
                    td.document_uri,
                    ri.kind,
                    ri.symbol,
                    ri.headline,
                    ri.structure,
                    substr(COALESCE(ri.headline || E'\n\n' || ri.structure, ri.headline, ri.structure, ''), 1, 640) as snippet,
                    ri.line_start,
                    ri.line_end,
                    ri.lang,
                    ri.mime as semantic_type,
                    0.5 as score,
                    ROW_NUMBER() OVER (
                        PARTITION BY td.document_uri
                        ORDER BY ri.line_start
                    ) as rn
                FROM repo_index ri
                JOIN target_docs td ON td.doc_id = ri.doc_id
                WHERE ri.scope = 'object'
            )
            SELECT
                uri, document_uri, kind, symbol, headline, structure, snippet,
                line_start, line_end, lang, semantic_type, score
            FROM objects
            WHERE rn <= {objectsPerDocument}
            ORDER BY document_uri, line_start
            """;

        var results = new List<ObjectMatch>();
        var rows = _db.Query(sql);

        foreach (var row in rows)
        {
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
                Score: Convert.ToDouble(row.GetValueOrDefault("score") ?? 0.5)
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
