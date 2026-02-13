using System.Globalization;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides semantic search within matched files as a read modifier.
/// Complexity: Queries existing search infrastructure with scope filtering,
/// extracts snippets with line context, and formats results for token budget.
/// </summary>
internal sealed class FindHandler(DuckDbDataStore db) : IModifierHandler
{
    private readonly DuckDbDataStore _db = db ?? throw new ArgumentNullException(nameof(db));

    private const int DefaultContextLines = 2;
    private const int MaxResults = 20;
    private const double MinScoreThreshold = 0.10;

    public string ModifierName => "find";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var keywords = parameter?.Trim();
        if (string.IsNullOrWhiteSpace(keywords))
        {
            return Task.FromResult(BuildSimpleResult(
                "Missing search keywords. Usage: <uri-pattern> => find: <keywords>",
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

        // Execute semantic search within scope
        var searchResults = ExecuteSearch(keywords, documentUris, ct);

        if (searchResults.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                $"No semantic matches for '{keywords}' in {documentUris.Count} file(s).",
                filesConsulted: documentUris,
                tokenBudget: tokenBudget));
        }

        // Filter to results above threshold
        var filteredResults = searchResults
            .Where(r => r.Score >= MinScoreThreshold)
            .OrderByDescending(r => r.Score)
            .Take(MaxResults)
            .ToList();

        var belowThreshold = searchResults.Count - filteredResults.Count;

        if (filteredResults.Count == 0)
        {
            var bestScore = searchResults.Max(r => r.Score);
            return Task.FromResult(BuildSimpleResult(
                $"No strong semantic matches for '{keywords}' in {documentUris.Count} file(s). Best score: {bestScore:F2} (threshold: {MinScoreThreshold:F2})",
                filesConsulted: documentUris,
                tokenBudget: tokenBudget,
                warning: "All matches below relevance threshold"));
        }

        // Build output with budget fitting
        var (content, shownCount) = BuildOutput(filteredResults, belowThreshold, tokenBudget, ct);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["matches_found"] = searchResults.Count,
            ["matches_shown"] = shownCount,
            ["below_threshold"] = belowThreshold,
            ["keywords"] = keywords
        };

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: filteredResults.Count,
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

            // Strip fragment from URI to get base document URI
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

    private IReadOnlyList<FindResult> ExecuteSearch(string keywords, IReadOnlyList<string> documentUris, CancellationToken ct)
    {
        var results = new List<FindResult>();

        if (documentUris.Count == 0)
            return results;

        try
        {
            var escapedKeywords = EscapeSqlLiteral(keywords);
            var escapedUriGlob = EscapeSqlLiteral(BuildDocumentUriGlob(documentUris));

            // Stage 1: Get initial semantic search results with chunk byte ranges
            // Stage 2: Refine with zoom_and_enhance (binary chop with JIT embeddings)
            var sql = $"""
                WITH initial_chunks AS (
                    SELECT
                        n.uri,
                        s.best_chunk_start,
                        s.best_chunk_end,
                        s.sem_score
                    FROM _search_semantic(
                        '{escapedKeywords}',
                        uri_glob := '{escapedUriGlob}',
                        max_cand := {MaxResults * 2}
                    ) s
                    JOIN node n ON n.id = s.node_id
                ),
                refined AS (
                    SELECT * FROM zoom_and_enhance(
                        (SELECT json_group_array(json_object(
                            'uri', uri,
                            'start_byte', best_chunk_start,
                            'end_byte', best_chunk_end,
                            'score', sem_score
                        )) FROM initial_chunks),
                        '{escapedKeywords}',
                        min_lines := 8,
                        max_depth := 2
                    )
                )
                SELECT
                    r.uri,
                    r.start_line AS line_start,
                    r.end_line AS line_end,
                    r.score,
                    r.depth,
                    a.headline,
                    (SELECT string_agg(s.text, E'\n' ORDER BY s.line_number)
                     FROM snippet(r.uri || '#line=' || r.start_line || ',' || r.end_line, {DefaultContextLines}) s
                    ) AS snippet
                FROM refined r
                JOIN node n ON n.uri = r.uri
                JOIN artifact a ON a.id = n.artifact_id
                ORDER BY r.score DESC
                LIMIT {MaxResults * 2}
                """;

            var rows = _db.Query(sql);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                var uri = row.TryGetValue("uri", out var uriVal) ? uriVal?.ToString() : null;
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                var headline = row.TryGetValue("headline", out var headlineVal) ? headlineVal?.ToString() : null;
                var snippet = row.TryGetValue("snippet", out var snippetVal) ? snippetVal?.ToString() : null;
                var lineStart = row.TryGetValue("line_start", out var lineStartVal) && lineStartVal is not null
                    ? Convert.ToInt32(lineStartVal, CultureInfo.InvariantCulture)
                    : (int?)null;
                var lineEnd = row.TryGetValue("line_end", out var lineEndVal) && lineEndVal is not null
                    ? Convert.ToInt32(lineEndVal, CultureInfo.InvariantCulture)
                    : (int?)null;
                var score = row.TryGetValue("score", out var scoreVal) && scoreVal is not null
                    ? Convert.ToDouble(scoreVal, CultureInfo.InvariantCulture)
                    : 0.0;

                results.Add(new FindResult(
                    Uri: uri!,
                    Headline: headline,
                    Snippet: snippet,
                    LineStart: lineStart,
                    LineEnd: lineEnd,
                    Score: score,
                    SemanticScore: score));
            }
        }
        catch (Exception ex) when (ex.Message.Contains("embeddings") ||
                                   ex.Message.Contains("embedding") ||
                                   ex.Message.Contains("not ready") ||
                                   ex.Message.Contains("zoom_and_enhance"))
        {
            // Embeddings not ready or zoom_and_enhance failed - fall back to basic search
            return ExecuteSearchFallback(keywords, documentUris, ct);
        }

        return results;
    }

    /// <summary>
    /// Fallback search without zoom_and_enhance refinement.
    /// Used when embeddings aren't ready or zoom_and_enhance fails.
    /// </summary>
    private IReadOnlyList<FindResult> ExecuteSearchFallback(string keywords, IReadOnlyList<string> documentUris, CancellationToken ct)
    {
        var results = new List<FindResult>();

        try
        {
            var escapedKeywords = EscapeSqlLiteral(keywords);
            var escapedUriGlob = EscapeSqlLiteral(BuildDocumentUriGlob(documentUris));

            // Use _search_candidates and filter with WHERE clause
            var sql = $"""
                SELECT
                    uri,
                    scope,
                    headline,
                    snippet,
                    line_start,
                    line_end,
                    score,
                    dense_score,
                    bm25_score
                FROM _search_candidates(
                    '{escapedKeywords}',
                    k := {MaxResults * 4},
                    uri_glob := '{escapedUriGlob}'
                )
                WHERE scope = 'document'
                ORDER BY score DESC
                LIMIT {MaxResults * 2}
                """;

            var rows = _db.Query(sql);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                var uri = row.TryGetValue("uri", out var uriVal) ? uriVal?.ToString() : null;
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                var headline = row.TryGetValue("headline", out var headlineVal) ? headlineVal?.ToString() : null;
                var snippet = row.TryGetValue("snippet", out var snippetVal) ? snippetVal?.ToString() : null;
                var lineStart = row.TryGetValue("line_start", out var lineStartVal) && lineStartVal is not null
                    ? Convert.ToInt32(lineStartVal, CultureInfo.InvariantCulture)
                    : (int?)null;
                var lineEnd = row.TryGetValue("line_end", out var lineEndVal) && lineEndVal is not null
                    ? Convert.ToInt32(lineEndVal, CultureInfo.InvariantCulture)
                    : (int?)null;
                var score = row.TryGetValue("score", out var scoreVal) && scoreVal is not null
                    ? Convert.ToDouble(scoreVal, CultureInfo.InvariantCulture)
                    : 0.0;
                var denseScore = row.TryGetValue("dense_score", out var denseVal) && denseVal is not null
                    ? Convert.ToDouble(denseVal, CultureInfo.InvariantCulture)
                    : 0.0;

                results.Add(new FindResult(
                    Uri: uri!,
                    Headline: headline,
                    Snippet: snippet,
                    LineStart: lineStart,
                    LineEnd: lineEnd,
                    Score: score,
                    SemanticScore: denseScore));
            }
        }
        catch
        {
            // Search failed completely
        }

        return results;
    }

    private static (string Content, int ShownCount) BuildOutput(
        IReadOnlyList<FindResult> results,
        int belowThreshold,
        int tokenBudget,
        CancellationToken ct)
    {
        if (results.Count == 0)
            return (BuildFooter(0, belowThreshold), 0);

        var builder = new StringBuilder();
        var includedResults = new List<FindResult>();

        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();

            var tentativeContent = BuildTentativeContent(includedResults, result, belowThreshold);
            var tentativeTokens = TokenEstimator.EstimateTokens(tentativeContent);

            if (tentativeTokens > tokenBudget && includedResults.Count > 0)
            {
                break;
            }

            includedResults.Add(result);
        }

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
        IReadOnlyList<FindResult> existing,
        FindResult newResult,
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

    private static string FormatResult(FindResult result)
    {
        var builder = new StringBuilder();

        // Header: URI with line range and score
        var uriWithFragment = result.Uri;
        if (result.LineStart.HasValue)
        {
            var fragment = result.LineEnd.HasValue && result.LineEnd != result.LineStart
                ? $"#line={result.LineStart},{result.LineEnd}"
                : $"#line={result.LineStart}";

            // Only add fragment if URI doesn't already have one
            if (!uriWithFragment.Contains('#'))
                uriWithFragment += fragment;
        }

        builder.Append(uriWithFragment);
        builder.Append("  [score: ");
        builder.Append(result.Score.ToString("F2", CultureInfo.InvariantCulture));
        builder.Append(']');

        // Headline if available
        if (!string.IsNullOrWhiteSpace(result.Headline))
        {
            builder.Append('\n');
            builder.Append("  ");
            builder.Append(result.Headline);
        }

        // Snippet with line numbers
        if (!string.IsNullOrWhiteSpace(result.Snippet))
        {
            builder.Append('\n');
            var lines = result.Snippet.Split('\n');
            var startLine = result.LineStart ?? 1;

            for (var i = 0; i < lines.Length && i < 15; i++)
            {
                var lineNum = startLine + i;
                var lineText = lines[i].TrimEnd('\r');

                // Determine if this is a focus line (within the matched range)
                var isFocus = result.LineStart.HasValue && result.LineEnd.HasValue &&
                              lineNum >= result.LineStart.Value && lineNum <= result.LineEnd.Value;

                builder.Append('\n');
                builder.Append(isFocus ? '>' : ' ');
                builder.Append(lineNum.ToString(CultureInfo.InvariantCulture).PadLeft(4));
                builder.Append(": ");
                builder.Append(lineText);
            }

            if (lines.Length > 15)
            {
                builder.Append("\n  ... (");
                builder.Append(lines.Length - 15);
                builder.Append(" more lines)");
            }
        }

        return builder.ToString();
    }

    private static string BuildFooter(int shown, int omitted)
    {
        var shownLabel = shown == 1 ? "match" : "matches";
        if (omitted > 0)
        {
            return $"[{shown} {shownLabel} shown, {omitted} more below threshold/budget]";
        }
        return $"[{shown} {shownLabel} shown]";
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string BuildDocumentUriGlob(IReadOnlyList<string> documentUris)
        => string.Join(";", documentUris);

    private sealed record FindResult(
        string Uri,
        string? Headline,
        string? Snippet,
        int? LineStart,
        int? LineEnd,
        double Score,
        double SemanticScore);
}
