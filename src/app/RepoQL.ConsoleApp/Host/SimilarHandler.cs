using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides semantic similarity search as a read modifier, finding files
/// similar to a seed document by comparing stored passage embeddings.
/// Complexity: Queries document_embedding for chunk-level cosine similarity using
/// stored embeddings only (never re-embeds). Filters candidates by matching dimension,
/// pushes scope into SQL via VALUES list CTE, uses adaptive threshold for cloud embedding
/// compatibility, and fits results to token budget.
/// </summary>
internal sealed class SimilarHandler(
    DuckDbDataStore? db,
    UriRegistry? uriRegistry,
    ILogger<SimilarHandler>? logger = null) : IModifierHandler
{
    private readonly DuckDbDataStore? _db = db;
    private readonly UriRegistry? _uriRegistry = uriRegistry;
    private readonly ILogger<SimilarHandler> _logger = logger ?? NullLogger<SimilarHandler>.Instance;

    private const int MaxResults = 20;
    private const int DefaultContextLines = 2;
    private const double AdaptiveThresholdFloor = 0.01;
    private const double AdaptiveThresholdFraction = 0.50;

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

        // Normalize URIs for SQL queries — document_embedding stores lowercase via NormalizeContainerKey
        var seedKey = RepoUri.NormalizeContainerKey(seedBase);
        var documentKeys = documentUris.Select(RepoUri.NormalizeContainerKey).Distinct().ToList();

        // Step 1: Resolve seed dimension
        var seedDimResult = ResolveSeedDimension(seedKey, ct);
        if (seedDimResult.Error is not null)
        {
            return Task.FromResult(BuildSimpleResult(
                seedDimResult.Error,
                filesConsulted: documentUris,
                tokenBudget: tokenBudget));
        }

        var seedDim = seedDimResult.Dimension;

        // Step 2: Execute similarity search with dimension filtering and scope in SQL
        var searchResult = ExecuteSimilaritySearch(seedUri, seedKey, seedDim, documentKeys, ct);
        if (searchResult.Error is not null)
        {
            return Task.FromResult(BuildSimpleResult(
                searchResult.Error,
                filesConsulted: documentUris,
                tokenBudget: tokenBudget));
        }

        var results = searchResult.Results;

        // Step 3: Apply adaptive threshold
        var threshold = ComputeAdaptiveThreshold(results);

        var filtered = results
            .Where(r => r.Similarity >= threshold)
            .OrderByDescending(r => r.Similarity)
            .Take(MaxResults)
            .ToList();

        if (filtered.Count == 0)
        {
            var bestScore = results.Count > 0
                ? results.Max(r => r.Similarity)
                : 0.0;
            return Task.FromResult(BuildSimpleResult(
                $"No similar files found for '{seedUri}' in {documentUris.Count} file(s). Best similarity: {bestScore:F4} (threshold: {threshold:F4})",
                filesConsulted: documentUris,
                tokenBudget: tokenBudget,
                warning: "All results below similarity threshold"));
        }

        var belowThreshold = results.Count - filtered.Count;
        var (content, shownCount) = BuildOutput(filtered, belowThreshold, tokenBudget, ct);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["seed_uri"] = seedUri,
            ["seed_dim"] = seedDim,
            ["results_found"] = results.Count,
            ["results_shown"] = shownCount,
            ["below_threshold"] = belowThreshold,
            ["adaptive_threshold"] = threshold
        };

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: filtered.Count,
            Shown: shownCount,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(documentUris, null, extra)));
    }

    /// <summary>
    /// Resolves the embedding dimension for the seed URI from document_embedding.
    /// Returns the dimension or an actionable error message.
    /// </summary>
    internal SeedDimResult ResolveSeedDimension(string seedBaseUri, CancellationToken ct)
    {
        if (_db is null)
            return new SeedDimResult(0, "Database not available. Cannot perform similarity search.");

        try
        {
            var escapedUri = EscapeSqlLiteral(seedBaseUri);
            var sql = $"""
                SELECT DISTINCT dim
                FROM document_embedding
                WHERE uri = '{escapedUri}'
                  AND embedding_type = 'full'
                """;

            var rows = _db.Query(sql, ct);
            if (rows.Count == 0)
            {
                return new SeedDimResult(0,
                    $"No stored embeddings found for seed '{seedBaseUri}'. The file may not have been embedded yet, " +
                    "or embeddings may have been cleared. Check embedding status with ::status.");
            }

            // Use the first dimension found (typically there's only one per URI)
            var dim = rows[0].TryGetValue("dim", out var dimVal) && dimVal is not null
                ? Convert.ToInt32(dimVal, CultureInfo.InvariantCulture)
                : 0;

            if (dim <= 0)
            {
                return new SeedDimResult(0,
                    $"Seed '{seedBaseUri}' has embeddings with invalid dimension ({dim}).");
            }

            return new SeedDimResult(dim, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to resolve seed dimension for {SeedUri}", seedBaseUri);
            return new SeedDimResult(0,
                $"Failed to resolve seed embedding dimension: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes the similarity search using stored embeddings only. Never calls embed_passage().
    /// Pushes candidate URI scope into SQL via VALUES list CTE and filters by matching dimension.
    /// </summary>
    internal SimilaritySearchResult ExecuteSimilaritySearch(
        string seedUri,
        string seedBase,
        int seedDim,
        IReadOnlyList<string> documentUris,
        CancellationToken ct)
    {
        if (_db is null)
            return new SimilaritySearchResult([], "Database not available. Cannot perform similarity search.");

        try
        {
            var escapedSeedBase = EscapeSqlLiteral(seedBase);

            // Build seed range CTE for fragment handling
            var seedRangeCte = BuildSeedRangeCte(seedUri, escapedSeedBase);

            // Build VALUES list for candidate URI scope
            var scopeValues = BuildScopeValuesList(documentUris, seedBase);

            var sql = $"""
                {seedRangeCte}
                seed_chunks AS (
                    SELECT de.embedding
                    FROM document_embedding de
                    CROSS JOIN seed_range sr
                    WHERE de.uri = '{escapedSeedBase}'
                      AND de.embedding_type = 'full'
                      AND de.dim = {seedDim}
                      AND (
                          sr.start_byte IS NULL
                          OR NOT (de.end_byte < sr.start_byte OR de.start_byte > sr.end_byte)
                      )
                ),
                scope_uris(uri) AS (
                    {scopeValues}
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
                    JOIN scope_uris su ON de.uri = su.uri
                    WHERE de.embedding_type = 'full'
                      AND de.dim = {seedDim}
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

            // Check for dimension mismatch: seed has embeddings but no candidates matched
            if (rows.Count == 0)
            {
                var dimCheckResult = CheckDimensionMismatch(escapedSeedBase, seedDim, documentUris, ct);
                if (dimCheckResult is not null)
                    return new SimilaritySearchResult([], dimCheckResult);
            }

            var results = new List<SimilarResult>();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                var uri = row.TryGetValue("uri", out var uriVal) ? uriVal?.ToString() : null;
                if (string.IsNullOrWhiteSpace(uri))
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

            return new SimilaritySearchResult(results, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Similarity search failed for seed {SeedUri} against {CandidateCount} candidates",
                seedUri, documentUris.Count);
            return new SimilaritySearchResult([],
                $"Similarity query failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether the zero-result outcome is due to a dimension mismatch between
    /// seed embeddings and candidate embeddings, returning a specific error if so.
    /// </summary>
    private string? CheckDimensionMismatch(
        string escapedSeedBase,
        int seedDim,
        IReadOnlyList<string> documentUris,
        CancellationToken ct)
    {
        if (_db is null)
            return null;

        try
        {
            var scopeValues = BuildScopeValuesList(documentUris, null);
            var dimSql = $"""
                WITH scope_uris(uri) AS (
                    {scopeValues}
                )
                SELECT DISTINCT de.dim
                FROM document_embedding de
                JOIN scope_uris su ON de.uri = su.uri
                WHERE de.embedding_type = 'full'
                LIMIT 10
                """;

            var dimRows = _db.Query(dimSql, ct);
            if (dimRows.Count == 0)
            {
                return $"No stored embeddings found for any of the {documentUris.Count} candidate file(s). " +
                       "Candidates may not have been embedded yet.";
            }

            var candidateDims = dimRows
                .Select(r => r.TryGetValue("dim", out var d) && d is not null
                    ? Convert.ToInt32(d, CultureInfo.InvariantCulture)
                    : 0)
                .Where(d => d > 0)
                .Distinct()
                .ToList();

            if (candidateDims.Count > 0 && !candidateDims.Contains(seedDim))
            {
                var dimList = string.Join(", ", candidateDims);
                return $"Dimension mismatch: seed has {seedDim}-dim embeddings, but candidates have {dimList}-dim embeddings. " +
                       "This usually means the seed and candidates were embedded with different models. " +
                       "Re-embed with the same model to compare.";
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to check dimension mismatch");
        }

        return null;
    }

    /// <summary>
    /// Builds a VALUES list for candidate URI scope filtering in SQL.
    /// Excludes the seed URI from candidates.
    /// </summary>
    internal static string BuildScopeValuesList(IReadOnlyList<string> documentUris, string? excludeUri)
    {
        var sb = new StringBuilder("VALUES ");
        var first = true;
        foreach (var uri in documentUris)
        {
            if (excludeUri is not null && string.Equals(uri, excludeUri, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!first)
                sb.Append(", ");
            sb.Append("('");
            sb.Append(EscapeSqlLiteral(uri));
            sb.Append("')");
            first = false;
        }

        // Handle edge case where all URIs were excluded
        if (first)
            sb.Append("(NULL)");

        return sb.ToString();
    }

    /// <summary>
    /// Computes adaptive threshold: max(floor, topScore * fraction).
    /// Cloud embeddings produce lower absolute similarity values, so a hard 0.10 threshold
    /// would filter out valid matches. This approach adapts to the score distribution.
    /// </summary>
    internal static double ComputeAdaptiveThreshold(IReadOnlyList<SimilarResult> rankedResults)
    {
        if (rankedResults.Count == 0)
            return AdaptiveThresholdFloor;

        var topScore = rankedResults.Max(r => r.Similarity);
        var relativeThreshold = topScore * AdaptiveThresholdFraction;
        return Math.Max(AdaptiveThresholdFloor, relativeThreshold);
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

        var seedBase = RepoUri.NormalizeContainerKey(StripFragment(seedUri));
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

    /// <summary>
    /// Builds a CTE that resolves the seed URI's fragment to a byte range.
    /// - No fragment: use range from first object to last object (excludes using directives)
    /// - #symbol=Name: look up span by symbol suffix
    /// - #line=X,Y: convert lines to bytes via artifact text
    /// </summary>
    internal static string BuildSeedRangeCte(string seedUri, string escapedSeedBase)
    {
        var fragment = ParseFragment(seedUri);

        if (fragment is null)
        {
            // File-level seed: use range from first object to last object,
            // excluding using directives and boilerplate before the first declaration.
            // MIN/MAX over empty set returns NULLs, which the overlap check treats as "all chunks".
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

    internal readonly record struct SeedFragment(string? Symbol, int StartLine, int EndLine);

    internal static SeedFragment? ParseFragment(string uri)
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

    internal static string StripFragment(string uri)
    {
        if (RepoUri.TryParse(uri, out var repoUri))
            return repoUri.Container.AbsoluteUri;

        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        return hashIndex > 0 ? uri[..hashIndex] : uri;
    }

    internal static (string Content, int ShownCount) BuildOutput(
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

    internal static string FormatResult(SimilarResult result)
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

    internal static string BuildFooter(int shown, int omitted)
    {
        var label = shown == 1 ? "similar file" : "similar files";
        if (omitted > 0)
            return $"[{shown} {label} shown, {omitted} more below threshold/budget]";
        return $"[{shown} {label} shown]";
    }

    internal static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    internal sealed record SimilarResult(
        string Uri,
        string? Headline,
        string? Snippet,
        int? LineStart,
        int? LineEnd,
        double Similarity);

    internal readonly record struct SeedDimResult(int Dimension, string? Error);

    internal sealed record SimilaritySearchResult(
        IReadOnlyList<SimilarResult> Results,
        string? Error);
}
