using System.Diagnostics;
using System.Text.RegularExpressions;
using RepoQL.Xray.Search;

namespace RepoQL.Xray;

/// <summary>
/// Orchestrates xray search and rendering operations.
/// </summary>
public sealed class XrayOrchestrator
{
    private readonly IXraySearchEngine _searchEngine;
    private readonly IJitObjectSearchService? _jitService;

    public XrayOrchestrator(
        IXraySearchEngine searchEngine,
        IJitObjectSearchService? jitService = null)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _jitService = jitService;
    }

    /// <summary>
    /// Execute an xray query and return both rendered output and structured results.
    /// </summary>
    /// <param name="query">The query parameters.</param>
    /// <param name="status">Current indexer status (caller provides).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="stopwatch">Optional stopwatch to capture elapsed time in output footer.</param>
    /// <returns>Execution result with rendered output and structured results.</returns>
    public async Task<XrayExecutionResult> ExecuteAsync(
        XrayQuery query,
        IndexerStatus status,
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
            Intent: query.Intent,
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
            status = status with { ElapsedMs = stopwatch.ElapsedMilliseconds };
        }

        if (searchResult.Results.Count == 0)
        {
            var noResults = string.IsNullOrWhiteSpace(query.Keywords)
                ? $"No results found in scope: {query.Scope ?? "(all)"}"
                : $"No results matching '{query.Keywords}' in scope: {query.Scope ?? "(all)"}";
            var emptyOutput = $"{noResults}\n\n{RepresentationFormatter.FormatStatusFooter(status)}";
            return new XrayExecutionResult(emptyOutput, [], Truncated: false);
        }

        // Convert SearchResult → XrayResult
        var xrayResults = searchResult.Results.Select(ToXrayResult).ToList();

        // De-duplicate: remove top-level results that already appear as children of other results
        xrayResults = DeduplicateResults(xrayResults);

        var hasSearchCriteria = !string.IsNullOrWhiteSpace(query.Keywords) || boostPatterns.Count > 0;

        // Hierarchical token allocation (files compete first, then children within each file)
        var decisions = ValueBasedAllocator.Allocate(xrayResults, query.TokenBudget, query.Intent);

        // Apply limit if specified
        var limitedDecisions = query.Limit.HasValue && query.Limit.Value > 0
            ? decisions.Take(query.Limit.Value).ToList()
            : decisions;

        // Calculate omitted count for truncation info
        var omittedCount = xrayResults.Count - limitedDecisions.Count;
        var decisionResult = new DecisionResult(limitedDecisions, omittedCount, null);

        // Compose output
        var renderedOutput = OutputComposer.Compose(decisionResult, hasSearchCriteria, status);

        // Determine truncation
        var truncated = omittedCount > 0;

        return new XrayExecutionResult(renderedOutput, xrayResults, truncated);
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
    /// Convert a SearchResult to XrayResult, including child objects recursively.
    /// </summary>
    private static XrayResult ToXrayResult(SearchResult result)
    {
        IReadOnlyList<XrayResult>? childObjects = null;
        if (result.ChildObjects is { Count: > 0 })
        {
            childObjects = result.ChildObjects.Select(ToXrayResult).ToList();
        }

        return new XrayResult(
            Uri: result.Uri,
            Confidence: result.Confidence,
            Kind: result.Scope == SearchScope.Symbol ? result.Kind : null,
            Headline: result.Headline,
            Structure: result.Structure,
            Snippet: result.Snippet,
            Lang: result.Lang,
            SemanticType: result.SemanticType,
            ChildObjects: childObjects
        );
    }

    /// <summary>
    /// Remove top-level results that already appear as children of other results.
    /// This prevents duplicate content when a document and its child objects both match.
    /// </summary>
    private static List<XrayResult> DeduplicateResults(List<XrayResult> results)
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
    private static void CollectChildUris(XrayResult result, HashSet<string> childUris)
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
