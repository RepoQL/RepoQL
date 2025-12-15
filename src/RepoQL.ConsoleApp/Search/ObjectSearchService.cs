using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Protocol;
using RepoQL.Xray.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// Executes object-level search within specific documents.
/// </summary>
internal sealed class ObjectSearchService : IObjectSearchService
{
    private readonly RepoQlClientProvider _clientProvider;

    public ObjectSearchService(RepoQlClientProvider clientProvider)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    }

    /// <summary>
    /// Score threshold for "strong" document matches that warrant embedding search.
    /// </summary>
    private const double StrongMatchThreshold = 0.4;

    public async Task<IReadOnlyList<ObjectMatch>> SearchInDocumentsAsync(
        IReadOnlyList<string> documentUris,
        string? question,
        int objectsPerDocument,
        CancellationToken cancellationToken)
    {
        if (documentUris.Count == 0)
            return [];

        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var hasQuestion = !string.IsNullOrWhiteSpace(question);

        var results = new List<ObjectMatch>();

        // Track how many objects we found per document via embedding search
        var objectCountByDoc = new Dictionary<string, int>();

        // For strong matches with a question, do ONE embedding search across all documents
        if (hasQuestion)
        {
            var allObjects = await SearchObjectsInDocumentsAsync(
                client, documentUris, question!, objectsPerDocument, cancellationToken).ConfigureAwait(false);

            foreach (var obj in allObjects)
            {
                results.Add(obj);
                objectCountByDoc.TryGetValue(obj.DocumentUri, out var count);
                objectCountByDoc[obj.DocumentUri] = count + 1;
            }
        }

        // For documents where we need more objects (embedding found none or fewer than requested)
        var docsNeedingMore = documentUris
            .Where(u => !objectCountByDoc.TryGetValue(u, out var count) || count < objectsPerDocument)
            .ToList();

        if (docsNeedingMore.Count > 0)
        {
            // Calculate how many more objects we need per document
            var remainingPerDoc = docsNeedingMore.ToDictionary(
                u => u,
                u => objectCountByDoc.TryGetValue(u, out var count) ? objectsPerDocument - count : objectsPerDocument);

            var fallbackObjects = await GetObjectsByPositionAsync(
                client, docsNeedingMore, objectsPerDocument, cancellationToken).ConfigureAwait(false);

            // Filter out objects we already have and respect per-doc limits
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

        return results;
    }

    /// <summary>
    /// Search for objects across multiple documents using a single embedding search.
    /// Returns top objects per document, respecting the objectsPerDocument limit.
    /// </summary>
    private async Task<List<ObjectMatch>> SearchObjectsInDocumentsAsync(
        IRepoQlClient client,
        IReadOnlyList<string> documentUris,
        string question,
        int objectsPerDocument,
        CancellationToken cancellationToken)
    {
        if (documentUris.Count == 0)
            return [];

        // Build URI pattern filter for all documents
        var uriPatterns = string.Join(" OR ", documentUris.Select((u, i) => $"s.uri LIKE ${i + 2}"));
        var totalLimit = documentUris.Count * objectsPerDocument * 2; // 2x buffer for ranking

        // Single search query covering all documents
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
                FROM search($1, k := {Math.Max(50, totalLimit)}) s
                WHERE s.scope = 'object'
                  AND ({uriPatterns})
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

        var parameters = new List<object?> { question };
        foreach (var docUri in documentUris)
        {
            parameters.Add(EscapeSql(docUri) + "#%");
        }

        try
        {
            var response = await client.ExecuteRawQueryAsync(
                sql, parameters.ToArray(), null, cancellationToken).ConfigureAwait(false);

            var results = new List<ObjectMatch>();
            foreach (var row in response.Rows)
            {
                var values = row.Values;
                if (values.Count < 12) continue;

                var uri = ExtractString(values[0]);
                var kind = ExtractString(values[2]);
                var docUri = ExtractString(values[11]);

                if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(docUri))
                    continue;

                results.Add(new ObjectMatch(
                    Uri: uri,
                    DocumentUri: docUri,
                    Kind: kind,
                    Symbol: ExtractString(values[1]),
                    Headline: ExtractString(values[3]),
                    Structure: ExtractString(values[4]),
                    Snippet: ExtractString(values[5]),
                    LineStart: ExtractInt(values[6]) ?? 1,
                    LineEnd: ExtractInt(values[7]) ?? 1,
                    Lang: ExtractString(values[8]),
                    SemanticType: ExtractString(values[9]),
                    Score: ExtractDouble(values[10]) ?? 0.5
                ));
            }

            return results;
        }
        catch
        {
            // If search fails, return empty - will fall back to position-based
            return [];
        }
    }

    /// <summary>
    /// Get objects from documents by position (fallback when embedding search finds nothing).
    /// </summary>
    private async Task<List<ObjectMatch>> GetObjectsByPositionAsync(
        IRepoQlClient client,
        IReadOnlyList<string> documentUris,
        int objectsPerDocument,
        CancellationToken cancellationToken)
    {
        if (documentUris.Count == 0)
            return [];

        var uriList = string.Join(",", documentUris.Select(u => $"'{EscapeSql(u)}'"));

        var sql = $"""
            WITH objects AS (
                SELECT
                    ri.uri,
                    CASE
                        WHEN position('#' IN ri.uri) > 0 THEN substring(ri.uri, 1, position('#' IN ri.uri) - 1)
                        ELSE ri.uri
                    END as document_uri,
                    ri.kind,
                    ri.symbol,
                    ri.headline,
                    ri.structure,
                    ri.body as snippet,
                    ri.line_start,
                    ri.line_end,
                    ri.lang,
                    ri.mime as semantic_type,
                    0.5 as score,
                    ROW_NUMBER() OVER (
                        PARTITION BY CASE
                            WHEN position('#' IN ri.uri) > 0 THEN substring(ri.uri, 1, position('#' IN ri.uri) - 1)
                            ELSE ri.uri
                        END
                        ORDER BY ri.line_start
                    ) as rn
                FROM repo_index ri
                WHERE ri.scope = 'object'
                  AND (CASE
                      WHEN position('#' IN ri.uri) > 0 THEN substring(ri.uri, 1, position('#' IN ri.uri) - 1)
                      ELSE ri.uri
                  END) IN ({uriList})
            )
            SELECT
                uri, document_uri, kind, symbol, headline, structure, snippet,
                line_start, line_end, lang, semantic_type, score
            FROM objects
            WHERE rn <= {objectsPerDocument}
            ORDER BY document_uri, line_start
            """;

        var response = await client.ExecuteRawQueryAsync(sql, [], null, cancellationToken).ConfigureAwait(false);

        var results = new List<ObjectMatch>();
        foreach (var row in response.Rows)
        {
            var values = row.Values;
            if (values.Count < 12) continue;

            var uri = ExtractString(values[0]);
            var docUri = ExtractString(values[1]);
            var kind = ExtractString(values[2]);

            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(docUri) || string.IsNullOrWhiteSpace(kind))
                continue;

            results.Add(new ObjectMatch(
                Uri: uri,
                DocumentUri: docUri,
                Kind: kind,
                Symbol: ExtractString(values[3]),
                Headline: ExtractString(values[4]),
                Structure: ExtractString(values[5]),
                Snippet: ExtractString(values[6]),
                LineStart: ExtractInt(values[7]) ?? 1,
                LineEnd: ExtractInt(values[8]) ?? 1,
                Lang: ExtractString(values[9]),
                SemanticType: ExtractString(values[10]),
                Score: ExtractDouble(values[11]) ?? 0.5
            ));
        }

        return results;
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
