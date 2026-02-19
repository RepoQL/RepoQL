using System.Globalization;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Contracts.Configuration;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provides semantic search within matched files as a read modifier.
/// Complexity: Delegates semantic narrowing to FindRefinementEngine and renders
/// line-anchored snippets under strict token-budget fitting.
/// </summary>
internal sealed class FindHandler(DuckDbDataStore db, RepoQlConfig? config = null) : IModifierHandler
{
    private const int DefaultMaxScopeDocuments = 96;
    private const int BroadScopeDocumentThreshold = 96;
    private const int BroadScopeShortlistMaxDocuments = 96;
    private const int BroadScopeShortlistTimeoutMs = 8_000;
    private const int BroadScopeTotalTimeoutMs = 40_000;
    private const int BroadScopeRoundTimeoutMs = 12_000;
    private const double AdaptiveThresholdFloor = 0.03;
    private const double AdaptiveThresholdFraction = 0.70;

    private readonly RepoQlConfig _config = config ?? new RepoQlConfig();
    private readonly FindRefinementEngine _engine = new(db);

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

        var maxScopeDocuments = Math.Clamp(
            _config.Find.MaxScopeDocuments ?? DefaultMaxScopeDocuments,
            1,
            10_000);
        if (documentUris.Count > maxScopeDocuments)
        {
            return Task.FromResult(BuildSimpleResult(
                BuildTooBroadScopeMessage(keywords, documentUris.Count, maxScopeDocuments),
                filesConsulted: documentUris,
                tokenBudget: tokenBudget,
                warning: "Find scope exceeds configured file limit"));
        }

        var settings = FindRuntimeSettings.From(_config.Find);
        var isBroadScope = documentUris.Count >= BroadScopeDocumentThreshold;
        if (isBroadScope)
        {
            settings = settings with
            {
                RoundTimeoutMs = Math.Min(settings.RoundTimeoutMs, BroadScopeRoundTimeoutMs),
                TotalTimeoutMs = Math.Min(settings.TotalTimeoutMs, BroadScopeTotalTimeoutMs)
            };
        }

        var refinedDocumentUris = documentUris;
        var preselectedDocuments = 0;

        if (isBroadScope)
        {
            var shortlistSize = Math.Clamp(
                Math.Max(settings.MaxResults * 4, 48),
                24,
                Math.Min(BroadScopeShortlistMaxDocuments, documentUris.Count));
            var shortlist = _engine.PreselectDocumentUris(
                keywords,
                documentUris,
                shortlistSize,
                Math.Min(settings.RoundTimeoutMs, BroadScopeShortlistTimeoutMs),
                ct);

            if (shortlist.Count > 0)
            {
                refinedDocumentUris = shortlist;
                preselectedDocuments = shortlist.Count;
            }
        }

        var searchOutcome = _engine.ExecuteAdaptiveSearch(keywords, refinedDocumentUris, settings, ct);

        if (searchOutcome.Results.Count == 0)
        {
            var warning = searchOutcome.DegradedReason is null
                ? null
                : $"Find degraded: {searchOutcome.DegradedReason}";

            var scopeHint = isBroadScope
                ? "\n\nHint: find is strongest on narrowed scopes. Use explore(intent=Inspect, keywords=...) to shortlist files, then run read(... => find: ...)."
                : string.Empty;

            return Task.FromResult(BuildSimpleResult(
                $"No semantic matches for '{keywords}' in {documentUris.Count} file(s).{scopeHint}",
                filesConsulted: documentUris,
                tokenBudget: tokenBudget,
                warning: warning));
        }

        var filteredResults = searchOutcome.Results
            .Where(r => r.Score >= settings.MinScoreThreshold)
            .OrderByDescending(r => r.Score)
            .Take(settings.MaxResults)
            .ToList();

        var scoreThresholdUsed = settings.MinScoreThreshold;
        var usedAdaptiveThreshold = false;

        if (filteredResults.Count == 0 && searchOutcome.Results.Count > 0)
        {
            scoreThresholdUsed = ComputeAdaptiveThreshold(searchOutcome.Results, settings.MinScoreThreshold);
            filteredResults = searchOutcome.Results
                .Where(r => r.Score >= scoreThresholdUsed)
                .OrderByDescending(r => r.Score)
                .Take(settings.MaxResults)
                .ToList();
            usedAdaptiveThreshold = filteredResults.Count > 0;
        }

        var belowThreshold = searchOutcome.Results.Count - filteredResults.Count;

        if (filteredResults.Count == 0)
        {
            var bestScore = searchOutcome.Results.Max(r => r.Score);
            var warning = searchOutcome.DegradedReason is null
                ? "All matches below relevance threshold"
                : $"All matches below relevance threshold; degraded: {searchOutcome.DegradedReason}";

            return Task.FromResult(BuildSimpleResult(
                $"No strong semantic matches for '{keywords}' in {documentUris.Count} file(s). Best score: {bestScore:F2} (threshold: {scoreThresholdUsed:F2})",
                filesConsulted: documentUris,
                tokenBudget: tokenBudget,
                warning: warning));
        }

        var (content, shownCount) = BuildOutput(filteredResults, belowThreshold, tokenBudget, ct);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["matches_found"] = searchOutcome.Results.Count,
            ["matches_shown"] = shownCount,
            ["below_threshold"] = belowThreshold,
            ["keywords"] = keywords,
            ["adaptive_rounds"] = searchOutcome.Rounds,
            ["adaptive_widenings"] = searchOutcome.Widenings,
            ["candidate_limit"] = searchOutcome.FinalCandidateLimit,
            ["fallback_used"] = searchOutcome.FallbackUsed,
            ["timed_out"] = searchOutcome.TimedOut,
            ["docs_total"] = documentUris.Count,
            ["docs_refined"] = refinedDocumentUris.Count,
            ["docs_preselected"] = preselectedDocuments,
            ["score_threshold"] = scoreThresholdUsed,
            ["adaptive_threshold_used"] = usedAdaptiveThreshold
        };

        if (!string.IsNullOrWhiteSpace(searchOutcome.DegradedReason))
            extra["degraded_reason"] = searchOutcome.DegradedReason!;

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: filteredResults.Count,
            Shown: shownCount,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(documentUris, null, extra)));
    }

    private static string BuildTooBroadScopeMessage(string keywords, int matchedFiles, int maxScopeDocuments)
    {
        var escapedKeywords = keywords.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"""
            Scope too broad for read => find: {matchedFiles} file(s) matched, limit is {maxScopeDocuments}.

            read => find is optimized for snippet extraction in a narrowed file set.
            Use explore(intent=Inspect, keywords="{escapedKeywords}") to find likely files first, then run read(... => find: ...).
            """;
    }

    private static double ComputeAdaptiveThreshold(
        IReadOnlyList<FindSemanticMatch> rankedResults,
        double configuredThreshold)
    {
        if (rankedResults.Count == 0)
            return configuredThreshold;

        var topScore = rankedResults[0].Score;
        var relativeThreshold = topScore * AdaptiveThresholdFraction;
        var threshold = Math.Min(configuredThreshold, relativeThreshold);
        return Math.Max(AdaptiveThresholdFloor, threshold);
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

    private static (string Content, int ShownCount) BuildOutput(
        IReadOnlyList<FindSemanticMatch> results,
        int belowThreshold,
        int tokenBudget,
        CancellationToken ct)
    {
        if (results.Count == 0)
            return (BuildFooter(0, belowThreshold), 0);

        var builder = new StringBuilder();
        var includedResults = new List<FindSemanticMatch>();

        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();

            var tentativeContent = BuildTentativeContent(includedResults, result, belowThreshold);
            var tentativeTokens = TokenEstimator.EstimateTokens(tentativeContent);

            if (tentativeTokens > tokenBudget && includedResults.Count > 0)
                break;

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
        IReadOnlyList<FindSemanticMatch> existing,
        FindSemanticMatch newResult,
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

    private static string FormatResult(FindSemanticMatch result)
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
        builder.Append("  [score: ");
        builder.Append(result.Score.ToString("F2", CultureInfo.InvariantCulture));
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

            for (var i = 0; i < lines.Length && i < 15; i++)
            {
                var lineNum = startLine + i;
                var lineText = lines[i].TrimEnd('\r');

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
            return $"[{shown} {shownLabel} shown, {omitted} more below threshold/budget]";

        return $"[{shown} {shownLabel} shown]";
    }
}
