using System.Text.RegularExpressions;
using RepoQL.Xray.Search;

namespace RepoQL.Xray;

/// <summary>
/// Orchestrates xray search and rendering operations.
/// </summary>
public sealed class XrayOrchestrator
{
    private readonly IXraySearchEngine _searchEngine;

    public XrayOrchestrator(IXraySearchEngine searchEngine)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
    }

    /// <summary>
    /// Execute an xray query and return both rendered output and structured results.
    /// </summary>
    /// <param name="query">The query parameters.</param>
    /// <param name="status">Current indexer status (caller provides).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result with rendered output and structured results.</returns>
    public async Task<XrayExecutionResult> ExecuteAsync(
        XrayQuery query,
        IndexerStatus status,
        CancellationToken cancellationToken)
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

        // Execute search
        var searchResult = await _searchEngine.SearchAsync(searchParams, cancellationToken).ConfigureAwait(false);

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

        // Build rendering context
        var hasSearchCriteria = !string.IsNullOrWhiteSpace(query.Keywords) || boostPatterns.Count > 0;
        var context = new RenderingContext(
            Intent: query.Intent,
            TokenBudget: query.TokenBudget,
            Limit: query.Limit,
            HasSearchCriteria: hasSearchCriteria,
            IndexerStatus: status
        );

        // Get decisions (gives us truncation info)
        var decisionResult = DecisionEngine.Decide(xrayResults, context);

        // Compose output
        var renderedOutput = OutputComposer.Compose(decisionResult, hasSearchCriteria, status);

        // Determine truncation
        var truncated = decisionResult.OmittedCount > 0;

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
}
