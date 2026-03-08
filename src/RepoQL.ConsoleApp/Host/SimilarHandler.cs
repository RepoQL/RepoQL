using System.Globalization;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides semantic similarity search as a read modifier, finding files
/// similar to a seed document by comparing stored passage embeddings.
/// Complexity: Queries document_embedding directly for chunk-level cosine similarity
/// (passage-to-passage), takes best chunk match per candidate, filters to scope, and
/// fits results to token budget.
/// </summary>
internal sealed class SimilarHandler(DuckDbDataStore? db, UriRegistry? uriRegistry) : IModifierHandler
{
    private readonly DuckDbDataStore? _db = db;
    private readonly UriRegistry? _uriRegistry = uriRegistry;

    private const int MaxResults = 20;
    private const int DefaultContextLines = 2;
    private const double MinSimilarityThreshold = 0.10;

    public string ModifierName => "similar";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var seedUri = parameter?.Trim();
        if (string.IsNullOrWhiteSpace(seedUri))
        {
            return Task.FromResult(BuildSimpleResult(
                "Missing seed URI. Usage: `<uri-pattern> => similar: <seed-uri>`",
                filesConsulted: [],
                tokenBudget: tokenBudget));
        }

        if (documents.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No files matched pattern.",
                filesConsulted: [],
                tokenBudget: tokenBudget));
        }

        var documentUris = ExtractDocumentUris(documents);
        if (documentUris.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No valid URIs found in matched documents.",
                filesConsulted: documents.Select(d => d.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        // Validate seed URI against registry before running the expensive search
        var seedBase = StripFragment(seedUri);
        var seedValidation = ValidateSeedUri(seedBase);
        if (seedValidation is not null)
        {
            return Task.FromResult(BuildSimpleResult(
                seedValidation,
                filesConsulted: documentUris,
                tokenBudget: tokenBudget));
        }

        var symbolValidation = ValidateSeedSymbol(seedUri, ct);
        if (symbolValidation is not null)
        {
            return Task.FromResult(BuildSimpleResult(
                symbolValidation,
                filesConsulted: documentUris,
                tokenBudget: tokenBudget));
        }

        var results = ExecuteSimilaritySearch(seedUri, documentUris, ct);

        var filtered = results
            .Where(r => r.Similarity >= MinSimilarityThreshold)
            .OrderByDescending(r => r.Similarity)
            .Take(MaxResults)
            .ToList();

        if (filtered.Count == 0)
        {
            var bestScore = results.Count > 0
                ? results.Max(r => r.Similarity)
                : 0.0;
            return Task.FromResult(BuildSimpleResult(
                $"No similar files found for '{seedUri}' in {documentUris.Count} file(s). Best similarity: {bestScore:F2}",
                filesConsulted: documentUris,
                tokenBudget: tokenBudget,
                warning: "All results below similarity threshold"));
        }

        var (content, shownCount) = BuildOutput(filtered, results.Count - filtered.Count, tokenBudget, ct);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["seed_uri"] = seedUri,
            ["results_found"] = results.Count,
            ["results_shown"] = shownCount,
            ["below_threshold"] = results.Count - filtered.Count
        };

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: filtered.Count,
            Shown: shownCount,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(documentUris, null, extra)));
    }

    private static ModifierResult BuildSimpleResult(
        string message,
        IReadOnlyList<string> filesConsulted,
        int tokenBudget,
        int totalAvailable = 0,
        int shown = 0,
        string? warning = null)
    {
        var tokenCount = TokenEstimator.EstimateTokens(message);
        return new ModifierResult(
            Content: message,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: shown,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(filesConsulted, warning, new Dictionary<string, object>()));
    }

    private static IReadOnlyList<string> ExtractDocumentUris(IReadOnlyList<ReadDocument> documents)
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Uri))
                continue;

            if (RepoUri.TryParse(doc.Uri, out var repoUri))
            {
                uris.Add(repoUri.Container.AbsoluteUri);
            }
            else
            {
                var hashIndex = doc.Uri.IndexOf('#', StringComparison.Ordinal);
                uris.Add(hashIndex > 0 ? doc.Uri[..hashIndex] : doc.Uri);
            }
        }

        return uris.ToList();
    }

    /// <summary>
    /// Validates the seed URI against the registry, returning an error message
    /// if the URI isn't found or embeddings aren't ready. Returns null if valid.
    /// </summary>
    private string? ValidateSeedUri(string seedBaseUri)
    {
        if (_uriRegistry is null)
            return null; // No registry available, let the query fail naturally

        if (!RepoUri.TryParse(seedBaseUri, out var repoUri))
            return $"Invalid seed URI: '{seedBaseUri}'";

        if (!_uriRegistry.TryGetValue(repoUri, out var entry))
            return $"Seed URI not found in index: '{seedBaseUri}'. Check the path is correct.";

        if (!entry.IsIndexed)
            return $"Seed file not yet indexed (status: {entry.Status}). Wait for indexing to complete.";

        if (entry.EmbeddingStatus is EmbeddingStatus.Pending or EmbeddingStatus.Embedding)
            return $"Embeddings not ready for seed (status: {entry.EmbeddingStatus}). Wait for embedding to complete.";

        if (entry.EmbeddingStatus == EmbeddingStatus.Failed)
            return $"Embedding failed for seed: {entry.Error ?? "unknown error"}";

        if (entry.EmbeddingStatus == EmbeddingStatus.NotApplicable)
            return $"Seed file has no embeddings (not applicable for embedding).";

        return null;
    }

    private string? ValidateSeedSymbol(string seedUri, CancellationToken ct)
    {
        if (_db is null)
            return null;

        ct.ThrowIfCancellationRequested();

        var fragment = ParseFragment(seedUri);
        if (fragment is null || fragment.Value.Symbol is null)
            return null;

        var seedBase = StripFragment(seedUri);
        var symbol = fragment.Value.Symbol;
        var symbolLower = symbol.ToLowerInvariant();
        var lastDot = symbolLower.LastIndexOf('.');
        var shortName = lastDot >= 0 ? symbolLower[(lastDot + 1)..] : symbolLower;

        var escapedSeedBase = EscapeSqlLiteral(seedBase);
        var escapedShort = EscapeSqlLiteral(shortName);
        var escapedFull = EscapeSqlLiteral(symbolLower);

        var existsSql = $"""
            SELECT 1
            FROM node doc
            JOIN span s ON s.document_id = doc.id
            JOIN node child ON child.span_id = s.id
            WHERE doc.uri = '{escapedSeedBase}' AND doc.kind = 'document'
              AND (LOWER(json_extract_string(child.properties, '$.name')) = '{escapedShort}'
                   OR LOWER(COALESCE(
                       json_extract_string(child.properties, '$.symbol'),
                       json_extract_string(child.properties, '$.name'),
                       '')) LIKE '%{escapedFull}%')
            LIMIT 1
            """;

        var exists = _db.Query(existsSql, ct).Any();
        if (exists)
            return null;

        var suggestionSql = $"""
            SELECT DISTINCT
                COALESCE(
                    json_extract_string(child.properties, '$.symbol'),
                    json_extract_string(child.properties, '$.name')
                ) AS symbol_name
            FROM node doc
            JOIN span s ON s.document_id = doc.id
            JOIN node child ON child.span_id = s.id
            WHERE doc.uri = '{escapedSeedBase}' AND doc.kind = 'document'
              AND child.kind <> 'document'
              AND COALESCE(
                    json_extract_string(child.properties, '$.symbol'),
                    json_extract_string(child.properties, '$.name')
                  ) IS NOT NULL
            ORDER BY
                levenshtein(
                    LOWER(COALESCE(
                        json_extract_string(child.properties, '$.name'),
                        json_extract_string(child.properties, '$.symbol')
                    )),
                    '{escapedShort}'
                ),
                LENGTH(COALESCE(
                    json_extract_string(child.properties, '$.name'),
                    json_extract_string(child.properties, '$.symbol')
                ))
            LIMIT 5
            """;

        var suggestions = _db.Query(suggestionSql, ct)
            .Select(row => row.TryGetValue("symbol_name", out var value) ? value?.ToString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        var suggestionText = suggestions.Count > 0
            ? string.Join(", ", suggestions.Select(s => $"'{s}'"))
            : "none";

        return $"Symbol '{symbol}' not found in file '{seedBase}'. Available symbols with similar names: {suggestionText}";
    }

    private IReadOnlyList<SimilarResult> ExecuteSimilaritySearch(
        string seedUri,
        IReadOnlyList<string> documentUris,
        CancellationToken ct)
    {
        if (_db is null)
            return [];

        try
        {
            var seedBase = StripFragment(seedUri);
            var escapedSeedBase = EscapeSqlLiteral(seedBase);
            var documentUriSet = new HashSet<string>(documentUris, StringComparer.OrdinalIgnoreCase);

            // Resolve fragment to byte range for chunk filtering
            var seedRangeCte = BuildSeedRangeCte(seedUri, escapedSeedBase);

            var sql = $"""
                {seedRangeCte}
                seed_chunks AS (
                    SELECT CASE
                        WHEN sr.start_byte IS NULL OR de.start_byte IS NULL
                            THEN de.embedding
                        WHEN de.start_byte >= sr.start_byte AND de.end_byte <= sr.end_byte
                            THEN de.embedding
                        ELSE embed_passage(substr(a.text_content,
                            GREATEST(de.start_byte, sr.start_byte) + 1,
                            LEAST(de.end_byte, sr.end_byte) - GREATEST(de.start_byte, sr.start_byte)))::FLOAT[]
                    END AS embedding
                    FROM document_embedding de
                    CROSS JOIN seed_range sr
                    JOIN node n ON n.uri = '{escapedSeedBase}' AND n.kind = 'document'
                    JOIN artifact a ON a.id = n.artifact_id
                    WHERE de.uri = '{escapedSeedBase}'
                      AND de.embedding_type = 'full'
                      AND (
                          sr.start_byte IS NULL
                          OR NOT (de.end_byte < sr.start_byte OR de.start_byte > sr.end_byte)
                      )
                ),
                chunk_pairs AS (
                    SELECT
                        de.uri,
                        de.node_id,
                        de.start_byte,
                        de.end_byte,
                        list_cosine_similarity(sc.embedding, de.embedding) AS similarity
                    FROM document_embedding de
                    CROSS JOIN seed_chunks sc
                    WHERE de.uri <> '{escapedSeedBase}'
                      AND de.embedding_type = 'full'
                ),
                best_per_doc AS (
                    SELECT *, ROW_NUMBER() OVER (PARTITION BY uri ORDER BY similarity DESC NULLS LAST) AS rn
                    FROM chunk_pairs
                    QUALIFY rn = 1
                    ORDER BY similarity DESC NULLS LAST
                    LIMIT {MaxResults * 2}
                )
                SELECT
                    bp.uri,
                    bp.similarity,
                    COALESCE(NULLIF(ri.headline, ''), NULLIF(ria.headline, '')) AS headline,
                    (SELECT string_agg(s.text, E'\n' ORDER BY s.line_number)
                     FROM snippet(bp.uri || CASE WHEN bp.start_byte IS NOT NULL
                         THEN '#char=' || bp.start_byte || ',' || bp.end_byte
                         ELSE '' END, {DefaultContextLines}) s
                    ) AS snippet,
                    (SELECT MIN(s.line_number)
                     FROM snippet(bp.uri || CASE WHEN bp.start_byte IS NOT NULL
                         THEN '#char=' || bp.start_byte || ',' || bp.end_byte
                         ELSE '' END, {DefaultContextLines}) s
                    ) AS line_start,
                    (SELECT MAX(s.line_number)
                     FROM snippet(bp.uri || CASE WHEN bp.start_byte IS NOT NULL
                         THEN '#char=' || bp.start_byte || ',' || bp.end_byte
                         ELSE '' END, {DefaultContextLines}) s
                    ) AS line_end
                FROM best_per_doc bp
                LEFT JOIN node ri ON ri.id = bp.node_id
                LEFT JOIN artifact ria ON ria.id = ri.artifact_id
                ORDER BY bp.similarity DESC NULLS LAST
                """;

            var rows = _db.Query(sql, ct);
            var results = new List<SimilarResult>();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                var uri = row.TryGetValue("uri", out var uriVal) ? uriVal?.ToString() : null;
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                if (!documentUriSet.Contains(uri!))
                    continue;

                var similarity = row.TryGetValue("similarity", out var simVal) && simVal is not null
                    ? Convert.ToDouble(simVal, CultureInfo.InvariantCulture)
                    : 0.0;

                var headline = row.TryGetValue("headline", out var headlineVal) ? headlineVal?.ToString() : null;
                var snippet = row.TryGetValue("snippet", out var snippetVal) ? snippetVal?.ToString() : null;
                var lineStart = row.TryGetValue("line_start", out var lsVal) && lsVal is not null
                    ? Convert.ToInt32(lsVal, CultureInfo.InvariantCulture)
                    : (int?)null;
                var lineEnd = row.TryGetValue("line_end", out var leVal) && leVal is not null
                    ? Convert.ToInt32(leVal, CultureInfo.InvariantCulture)
                    : (int?)null;

                results.Add(new SimilarResult(
                    Uri: uri!, Headline: headline, Snippet: snippet,
                    LineStart: lineStart, LineEnd: lineEnd, Similarity: similarity));
            }

            return results;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// Builds a CTE that resolves the seed URI's fragment to a byte range.
    /// - No fragment → first-to-last object byte range (excludes using directives)
    /// - #symbol=Name → look up span by symbol suffix
    /// - #line=X,Y → convert lines to bytes via artifact text
    /// </summary>
    private static string BuildSeedRangeCte(string seedUri, string escapedSeedBase)
    {
        var fragment = ParseFragment(seedUri);

        if (fragment is null)
        {
            // File-level seed: use range from first object to last object,
            // excluding using directives and boilerplate before the first declaration.
            // MIN/MAX over empty set returns NULLs, which the EXISTS check treats as "all chunks".
            return $"""
                WITH seed_range AS (
                    SELECT MIN(s.start_byte) AS start_byte, MAX(s.end_byte) AS end_byte
                    FROM node doc
                    JOIN span s ON s.document_id = doc.id
                    JOIN node child ON child.span_id = s.id
                    WHERE doc.uri = '{escapedSeedBase}' AND doc.kind = 'document'
                      AND child.kind <> 'document'
                ),
                """;
        }

        if (fragment.Value.Symbol is not null)
        {
            // Agents use dotted names like "FindHandler.ExecuteSearch" but
            // $.name stores just "ExecuteSearch". Match the last segment
            // against $.name, full string against the node URI's symbol.
            var symbolLower = fragment.Value.Symbol.ToLowerInvariant();
            var lastDot = symbolLower.LastIndexOf('.');
            var shortName = lastDot >= 0 ? symbolLower[(lastDot + 1)..] : symbolLower;
            var escapedShort = EscapeSqlLiteral(shortName);
            var escapedFull = EscapeSqlLiteral(symbolLower);
            return $"""
                WITH seed_range AS (
                    SELECT s.start_byte, s.end_byte
                    FROM node doc
                    JOIN span s ON s.document_id = doc.id
                    JOIN node child ON child.span_id = s.id
                    WHERE doc.uri = '{escapedSeedBase}' AND doc.kind = 'document'
                      AND (LOWER(json_extract_string(child.properties, '$.name')) = '{escapedShort}'
                           OR LOWER(COALESCE(
                               json_extract_string(child.properties, '$.symbol'),
                               json_extract_string(child.properties, '$.name'),
                               '')) LIKE '%{escapedFull}')
                    ORDER BY (s.end_byte - s.start_byte) DESC
                    LIMIT 1
                ),
                """;
        }

        // Line range: convert to bytes using list_transform on split lines
        return $"""
            WITH seed_range AS (
                SELECT
                    COALESCE(list_sum(list_transform(lines[:{fragment.Value.StartLine - 1}], x -> length(x) + 1)), 0)::BIGINT AS start_byte,
                    COALESCE(list_sum(list_transform(lines[:{fragment.Value.EndLine}], x -> length(x) + 1)), 0)::BIGINT AS end_byte
                FROM (
                    SELECT string_split(a.text_content, chr(10)) AS lines
                    FROM node n
                    JOIN artifact a ON a.id = n.artifact_id
                    WHERE n.uri = '{escapedSeedBase}' AND n.kind = 'document'
                )
            ),
            """;
    }

    private readonly record struct SeedFragment(string? Symbol, int StartLine, int EndLine);

    private static SeedFragment? ParseFragment(string uri)
    {
        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex < 0)
            return null;

        var fragment = uri[(hashIndex + 1)..];
        string? symbol = null;
        int? startLine = null;
        int? endLine = null;

        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("symbol=", StringComparison.OrdinalIgnoreCase))
                symbol = part[7..];
            else if (part.StartsWith("line=", StringComparison.OrdinalIgnoreCase))
            {
                var lineSpec = part[5..];
                var comma = lineSpec.IndexOf(',', StringComparison.Ordinal);
                if (comma > 0)
                {
                    int.TryParse(lineSpec[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s);
                    int.TryParse(lineSpec[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e);
                    startLine = s;
                    endLine = e;
                }
                else if (int.TryParse(lineSpec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
                {
                    startLine = single;
                    endLine = single;
                }
            }
        }

        if (symbol is not null)
            return new SeedFragment(symbol, 0, 0);
        if (startLine.HasValue)
            return new SeedFragment(null, startLine.Value, endLine ?? startLine.Value);

        return null;
    }

    private static string StripFragment(string uri)
    {
        if (RepoUri.TryParse(uri, out var repoUri))
            return repoUri.Container.AbsoluteUri;

        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        return hashIndex > 0 ? uri[..hashIndex] : uri;
    }

    private static (string Content, int ShownCount) BuildOutput(
        IReadOnlyList<SimilarResult> results,
        int belowThreshold,
        int tokenBudget,
        CancellationToken ct)
    {
        if (results.Count == 0)
            return (BuildFooter(0, belowThreshold), 0);

        var includedResults = new List<SimilarResult>();

        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();

            var tentativeContent = BuildTentativeContent(includedResults, result, belowThreshold);
            var tentativeTokens = TokenEstimator.EstimateTokens(tentativeContent);

            if (tentativeTokens > tokenBudget && includedResults.Count > 0)
                break;

            includedResults.Add(result);
        }

        var builder = new StringBuilder();
        for (var i = 0; i < includedResults.Count; i++)
        {
            if (i > 0)
                builder.Append("\n\n");
            builder.Append(FormatResult(includedResults[i]));
        }

        builder.Append("\n\n");
        builder.Append(BuildFooter(includedResults.Count, belowThreshold + (results.Count - includedResults.Count)));

        return (builder.ToString(), includedResults.Count);
    }

    private static string BuildTentativeContent(
        IReadOnlyList<SimilarResult> existing,
        SimilarResult newResult,
        int belowThreshold)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < existing.Count; i++)
        {
            if (i > 0)
                builder.Append("\n\n");
            builder.Append(FormatResult(existing[i]));
        }

        if (existing.Count > 0)
            builder.Append("\n\n");
        builder.Append(FormatResult(newResult));

        builder.Append("\n\n");
        builder.Append(BuildFooter(existing.Count + 1, belowThreshold));

        return builder.ToString();
    }

    private static string FormatResult(SimilarResult result)
    {
        var builder = new StringBuilder();

        var uriWithFragment = result.Uri;
        if (result.LineStart.HasValue)
        {
            var fragment = result.LineEnd.HasValue && result.LineEnd != result.LineStart
                ? $"#line={result.LineStart},{result.LineEnd}"
                : $"#line={result.LineStart}";

            if (!uriWithFragment.Contains('#'))
                uriWithFragment += fragment;
        }

        builder.Append(uriWithFragment);
        builder.Append("  [similarity: ");
        builder.Append(result.Similarity.ToString("F2", CultureInfo.InvariantCulture));
        builder.Append(']');

        if (!string.IsNullOrWhiteSpace(result.Headline))
        {
            builder.Append('\n');
            builder.Append("  ");
            builder.Append(result.Headline);
        }

        if (!string.IsNullOrWhiteSpace(result.Snippet))
        {
            builder.Append('\n');
            var lines = result.Snippet.Split('\n');
            var startLine = result.LineStart ?? 1;

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNum = startLine + i;
                var lineText = lines[i].TrimEnd('\r');

                builder.Append('\n');
                builder.Append(lineNum.ToString(CultureInfo.InvariantCulture).PadRight(5));
                builder.Append(lineText);
            }
        }

        return builder.ToString();
    }

    private static string BuildFooter(int shown, int omitted)
    {
        var label = shown == 1 ? "similar file" : "similar files";
        if (omitted > 0)
            return $"[{shown} {label} shown, {omitted} more below threshold/budget]";
        return $"[{shown} {label} shown]";
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record SimilarResult(
        string Uri,
        string? Headline,
        string? Snippet,
        int? LineStart,
        int? LineEnd,
        double Similarity);

}
