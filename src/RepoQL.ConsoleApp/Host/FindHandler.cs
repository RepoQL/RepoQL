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
    private const double MinScoreThreshold = 0.20;

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
                "Missing search keywords. Usage: file:///path => find: <keywords>",
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

        var fileUris = ExtractFileUris(documents);
        if (fileUris.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "Find is only available for file:/// URIs.",
                filesConsulted: documents.Select(d => d.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        // Build scope pattern for search
        var scopePattern = BuildScopePattern(fileUris);

        // Execute semantic search within scope
        var searchResults = ExecuteSearch(keywords, scopePattern, ct);

        if (searchResults.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                $"No semantic matches for '{keywords}' in {fileUris.Count} file(s).",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        // Filter to results above threshold and within matched files
        var filteredResults = searchResults
            .Where(r => r.Score >= MinScoreThreshold)
            .Where(r => fileUris.Any(uri =>
                r.Uri.StartsWith(uri, StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith(r.Uri, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Score)
            .Take(MaxResults)
            .ToList();

        var belowThreshold = searchResults.Count - filteredResults.Count;

        if (filteredResults.Count == 0)
        {
            var bestScore = searchResults.Max(r => r.Score);
            return Task.FromResult(BuildSimpleResult(
                $"No strong semantic matches for '{keywords}' in {fileUris.Count} file(s). Best score: {bestScore:F2} (threshold: {MinScoreThreshold:F2})",
                filesConsulted: fileUris,
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
            Metadata: new ResultMetadata(fileUris, null, extra)));
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

    private static IReadOnlyList<string> ExtractFileUris(IReadOnlyList<ReadDocument> documents)
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Uri))
                continue;

            if (RepoUri.TryParse(doc.Uri, out var repoUri))
            {
                if (string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                {
                    uris.Add(repoUri.Container.AbsoluteUri);
                }
            }
            else if (doc.Uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var hashIndex = doc.Uri.IndexOf('#', StringComparison.Ordinal);
                uris.Add(hashIndex > 0 ? doc.Uri[..hashIndex] : doc.Uri);
            }
        }

        return uris.ToList();
    }

    private static string BuildScopePattern(IReadOnlyList<string> fileUris)
    {
        if (fileUris.Count == 1)
        {
            return fileUris[0] + "%";
        }

        // For multiple files, find common prefix
        var commonPrefix = FindCommonPrefix(fileUris);
        if (!string.IsNullOrEmpty(commonPrefix) && commonPrefix.Length > 10)
        {
            return commonPrefix + "%";
        }

        // Fallback to file:/// prefix
        return "file:///%";
    }

    private static string FindCommonPrefix(IReadOnlyList<string> strings)
    {
        if (strings.Count == 0)
            return string.Empty;

        var first = strings[0];
        var prefixLength = first.Length;

        foreach (var s in strings.Skip(1))
        {
            prefixLength = Math.Min(prefixLength, s.Length);
            for (var i = 0; i < prefixLength; i++)
            {
                if (char.ToLowerInvariant(first[i]) != char.ToLowerInvariant(s[i]))
                {
                    prefixLength = i;
                    break;
                }
            }
        }

        return first[..prefixLength];
    }

    private IReadOnlyList<FindResult> ExecuteSearch(string keywords, string scopePattern, CancellationToken ct)
    {
        var results = new List<FindResult>();

        try
        {
            var escapedKeywords = EscapeSqlLiteral(keywords);
            var escapedScope = EscapeSqlLiteral(scopePattern);

            // Use _search_candidates with scope filtering
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
                    uri_glob := '{escapedScope}',
                    k := {MaxResults * 2}
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
        catch (Exception ex) when (ex.Message.Contains("embeddings") ||
                                   ex.Message.Contains("embedding") ||
                                   ex.Message.Contains("not ready"))
        {
            // Embeddings not ready - return empty results with appropriate message
            return results;
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

    private sealed record FindResult(
        string Uri,
        string? Headline,
        string? Snippet,
        int? LineStart,
        int? LineEnd,
        double Score,
        double SemanticScore);
}
