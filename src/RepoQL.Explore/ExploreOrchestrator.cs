using System.Diagnostics;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Explore.Search;

namespace RepoQL.Explore;

/// <summary>
/// Orchestrates explore search and rendering operations.
/// </summary>
public sealed class ExploreOrchestrator
{
    private readonly IExploreSearchEngine _searchEngine;
    private readonly IJitObjectSearchService? _jitService;
    private readonly ILlmProvider? _llmProvider;

    private const double StrongQualityThresholdRawScore = 0.70;
    private const double ModerateQualityThresholdRawScore = 0.40;
    private const double WeakQualityThresholdRawScore = 0.0;
    private const double CoverageThresholdRawScore = 0.40;
    private const int CoverageMinDocumentScope = 20;

    public ExploreOrchestrator(
        IExploreSearchEngine searchEngine,
        IJitObjectSearchService? jitService = null,
        ILlmProvider? llmProvider = null)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _jitService = jitService;
        _llmProvider = llmProvider;
    }

    /// <summary>
    /// Execute an explore query and return both rendered output and structured results.
    /// </summary>
    /// <param name="query">The query parameters.</param>
    /// <param name="status">Current indexer status (caller provides).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="stopwatch">Optional stopwatch to capture elapsed time in output footer.</param>
    /// <returns>Execution result with rendered output and structured results.</returns>
    public async Task<ExploreExecutionResult> ExecuteAsync(
        ExploreQuery query,
        TrustSignal status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch = null)
    {
        if (query.TokenBudget <= 0)
            throw new ArgumentException("tokenBudget must be a positive integer.", nameof(query));

        // Parse patterns
        var boostPatterns = ParsePatterns(query.Boost);
        var penalizePatterns = ParsePatterns(query.Penalize);

        // Build search parameters
        var searchParams = new SearchParameters(
            Scope: query.Scope,
            Question: query.Keywords,
            Patterns: boostPatterns,
            Breadth: query.Breadth,
            TokenBudget: query.TokenBudget,
            PenalizePatterns: penalizePatterns.Count > 0 ? penalizePatterns : null
        );

        // Create JIT cache for this search session
        var jitCache = _jitService is not null ? new JitEmbeddingCache() : null;

        // Execute search
        var searchResult = await _searchEngine.SearchAsync(
            searchParams,
            _jitService,
            jitCache,
            cancellationToken).ConfigureAwait(false);

        // Update status with actual elapsed time if stopwatch provided
        if (stopwatch is not null)
        {
            stopwatch.Stop();
            status = status with { ExecutionTimeMs = stopwatch.ElapsedMilliseconds };
        }

        if (searchResult.Results.Count == 0)
        {
            var noResults = string.IsNullOrWhiteSpace(query.Keywords)
                ? $"No results found in scope: {query.Scope ?? "(all)"}"
                : $"No results matching '{query.Keywords}' in scope: {query.Scope ?? "(all)"}";
            var emptyOutput = $"{noResults}\n\n{RepresentationFormatter.FormatStatusFooter(status)}";
            return new ExploreExecutionResult(emptyOutput, [], Truncated: false);
        }

        status = EnrichTrustSignal(status, query, boostPatterns, searchResult);

        // Convert SearchResult → ExploreResult
        var exploreResults = searchResult.Results.Select(ToExploreResult).ToList();

        // De-duplicate: remove top-level results that already appear as children of other results
        exploreResults = DeduplicateResults(exploreResults);

        var hasSearchCriteria = !string.IsNullOrWhiteSpace(query.Keywords) || boostPatterns.Count > 0;

        // Hierarchical token allocation (files compete first, then children within each file)
        var decisions = ValueBasedAllocator.Allocate(exploreResults, query.TokenBudget, query.Breadth);

        // Apply limit if specified
        var limitedDecisions = query.Limit.HasValue && query.Limit.Value > 0
            ? decisions.Take(query.Limit.Value).ToList()
            : decisions;

        // Calculate omitted count for truncation info
        var omittedCount = exploreResults.Count - limitedDecisions.Count;
        var decisionResult = new DecisionResult(limitedDecisions, omittedCount, null);

        // Compose output
        var renderedOutput = OutputComposer.Compose(decisionResult, hasSearchCriteria, status);
        var truncated = omittedCount > 0;

        return new ExploreExecutionResult(renderedOutput, exploreResults, truncated);
    }

    private static TrustSignal EnrichTrustSignal(
        TrustSignal status,
        ExploreQuery query,
        IReadOnlyList<string> boostPatterns,
        SearchEngineResult searchResult)
    {
        var isPureInventory = string.IsNullOrWhiteSpace(query.Keywords) && boostPatterns.Count == 0;
        string? qualityTier = null;

        if (isPureInventory)
        {
            qualityTier = "exhaustive";
        }
        else if (searchResult.Results.Count > 0)
        {
            var topRawScore = searchResult.Results[0].RawScore;
            qualityTier = topRawScore switch
            {
                > StrongQualityThresholdRawScore => "strong",
                > ModerateQualityThresholdRawScore => "moderate",
                > WeakQualityThresholdRawScore => "weak",
                _ => null
            };
        }

        int? coverageAboveThreshold = null;
        int? coverageTotalDocuments = null;
        var coverageAllInScope = false;

        if (!string.IsNullOrWhiteSpace(query.Keywords) && searchResult.TotalDocumentsMatched >= CoverageMinDocumentScope)
        {
            var bestScoreByDocument = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in searchResult.Results)
            {
                var documentUri = ToDocumentUri(result.Uri);
                if (bestScoreByDocument.TryGetValue(documentUri, out var existing) && existing >= result.RawScore)
                    continue;

                bestScoreByDocument[documentUri] = result.RawScore;
            }

            var scoredDocumentCount = bestScoreByDocument.Count;
            var aboveCount = bestScoreByDocument.Count(kvp => kvp.Value > CoverageThresholdRawScore);
            coverageTotalDocuments = searchResult.TotalDocumentsMatched;
            coverageAboveThreshold = Math.Min(aboveCount, coverageTotalDocuments.Value);
            coverageAllInScope = scoredDocumentCount > 0 && aboveCount == scoredDocumentCount;
        }

        return status with
        {
            SearchQualityTier = qualityTier,
            CoverageAboveThreshold = coverageAboveThreshold,
            CoverageTotalDocuments = coverageTotalDocuments,
            CoverageAllInScope = coverageAllInScope
        };
    }

    private static string ToDocumentUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return uri;

        if (RepoUri.TryParse(uri, out var parsed))
            return parsed.Container.AbsoluteUri;

        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? uri[..hash] : uri;
    }

    /// <summary>
    /// Parse comma-separated regex patterns, validating each one.
    /// Invalid patterns are silently skipped.
    /// </summary>
    private static IReadOnlyList<string> ParsePatterns(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
            return [];

        var result = new List<string>();
        foreach (var pattern in patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                result.Add(pattern);
            }
            catch (RegexParseException)
            {
                // Skip invalid patterns
            }
        }
        return result;
    }

    /// <summary>
    /// Convert a SearchResult to ExploreResult, including child objects recursively.
    /// </summary>
    private static ExploreResult ToExploreResult(SearchResult result)
    {
        IReadOnlyList<ExploreResult>? childObjects = null;
        if (result.ChildObjects is { Count: > 0 })
        {
            childObjects = result.ChildObjects.Select(ToExploreResult).ToList();
        }

        return new ExploreResult(
            Uri: result.Uri,
            Confidence: result.Confidence,
            Kind: result.Scope == SearchScope.Symbol ? result.Kind : null,
            Headline: result.Headline,
            Structure: result.Structure,
            Snippet: result.Snippet,
            Lang: result.Lang,
            SemanticType: result.SemanticType,
            ChildObjects: childObjects,
            Provenance: result.Provenance
        );
    }

    /// <summary>
    /// Remove top-level results that already appear as children of other results.
    /// This prevents duplicate content when a document and its child objects both match.
    /// </summary>
    private static List<ExploreResult> DeduplicateResults(List<ExploreResult> results)
    {
        if (results.Count == 0)
            return results;

        // Collect all URIs that appear as children (recursively)
        var childUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            CollectChildUris(result, childUris);
        }

        // Filter out top-level results whose URI appears as a child
        return results.Where(r => !childUris.Contains(r.Uri)).ToList();
    }

    /// <summary>
    /// Recursively collect all child URIs from a result.
    /// </summary>
    private static void CollectChildUris(ExploreResult result, HashSet<string> childUris)
    {
        if (result.ChildObjects is null || result.ChildObjects.Count == 0)
            return;

        foreach (var child in result.ChildObjects)
        {
            childUris.Add(child.Uri);
            CollectChildUris(child, childUris);
        }
    }
}
