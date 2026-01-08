using System.Diagnostics;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Xray.Search;

namespace RepoQL.Xray;

/// <summary>
/// Orchestrates xray search and rendering operations.
/// </summary>
public sealed class XrayOrchestrator
{
    private readonly IXraySearchEngine _searchEngine;
    private readonly IJitObjectSearchService? _jitService;
    private readonly ILlmProvider? _llmProvider;

    /// <summary>
    /// Minimum token budget for Understand intent to ensure sufficient context for LLM synthesis.
    /// </summary>
    private const int UnderstandMinBudget = 3000;

    public XrayOrchestrator(
        IXraySearchEngine searchEngine,
        IJitObjectSearchService? jitService = null,
        ILlmProvider? llmProvider = null)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _jitService = jitService;
        _llmProvider = llmProvider;
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

        // Handle Understand intent: check LLM provider and auto-scale budget
        var isUnderstand = query.Intent == Intent.Understand;
        if (isUnderstand)
        {
            if (_llmProvider is null || !_llmProvider.Enabled)
            {
                // LLM not configured - return error with guidance
                var errorMsg = """
                    LLM not configured. The 'understand' intent requires an LLM provider.

                    Set OPENROUTER_API_KEY environment variable to enable LLM synthesis.

                    Alternatively, use intent=examine for detailed structured results.
                    """;
                return new XrayExecutionResult(errorMsg, [], Truncated: false);
            }

            if (string.IsNullOrWhiteSpace(query.Keywords))
            {
                var errorMsg = """
                    Keywords required for 'understand' intent. The keywords become the question for LLM synthesis.

                    Example: xray(tokenBudget=2000, intent=understand, keywords="How does authentication work?")
                    """;
                return new XrayExecutionResult(errorMsg, [], Truncated: false);
            }
        }

        // Auto-scale budget for Understand intent
        var effectiveBudget = isUnderstand
            ? Math.Max(query.TokenBudget, UnderstandMinBudget)
            : query.TokenBudget;

        // For Understand, we use Find behavior internally for search/allocation
        var searchIntent = isUnderstand ? Intent.Find : query.Intent;

        // Parse patterns
        var boostPatterns = ParsePatterns(query.Boost);
        var penalizePatterns = ParsePatterns(query.Penalize);

        // For Understand intent, extract optimized search keywords from the question
        var searchKeywords = query.Keywords;
        if (isUnderstand && _llmProvider is not null && !string.IsNullOrWhiteSpace(query.Keywords))
        {
            try
            {
                searchKeywords = await _llmProvider.ExtractKeywordsAsync(query.Keywords, cancellationToken);
            }
            catch
            {
                // Fallback to original keywords on failure
            }
        }

        // Build search parameters
        var searchParams = new SearchParameters(
            Scope: query.Scope,
            Question: searchKeywords,
            Patterns: boostPatterns,
            Intent: searchIntent,
            TokenBudget: effectiveBudget,
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
        var decisions = ValueBasedAllocator.Allocate(xrayResults, effectiveBudget, searchIntent);

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

        // If Understand intent, synthesize via LLM
        if (isUnderstand && _llmProvider is not null)
        {
            // Re-render with LLM-friendly token budget (50k tokens ~= 200k chars for context)
            const int llmTokenBudget = 50_000;
            var llmDecisions = ValueBasedAllocator.Allocate(xrayResults, llmTokenBudget, Intent.Examine);
            var llmDecisionResult = new DecisionResult(llmDecisions, 0, null);
            var llmOutput = OutputComposer.Compose(llmDecisionResult, hasSearchCriteria, status);

            var synthesized = await SynthesizeUnderstandingAsync(
                llmOutput,
                query.Keywords!,
                status,
                cancellationToken).ConfigureAwait(false);
            return new XrayExecutionResult(synthesized, xrayResults, truncated);
        }

        return new XrayExecutionResult(renderedOutput, xrayResults, truncated);
    }

    /// <summary>
    /// Synthesize understanding from xray output using LLM.
    /// </summary>
    private async Task<string> SynthesizeUnderstandingAsync(
        string xrayOutput,
        string question,
        IndexerStatus status,
        CancellationToken ct)
    {
        // The xray output becomes the context, the keywords become the question
        // System prompt (CoreSystemPrompt with capsules) handles format and wisdom
        var intent = question;

        try
        {
            var result = await _llmProvider!.SummarizeAsync(
                xrayOutput,
                intent,
                maxTokens: 1000,
                repoTree: null,
                ct: ct).ConfigureAwait(false);

            // Calculate token count for the response content (excluding footer)
            var responseContent = $"## Understanding: {question}\n\n{result}";
            var tokenCount = TokenEstimator.EstimateTokens(responseContent);
            var footer = RepresentationFormatter.FormatStatusFooter(status, tokenCount);

            return $"""
                ## Understanding: {question}

                {result}

                ---

                {footer}
                """;
        }
        catch (Exception ex)
        {
            // Fall back to structured output on LLM failure
            return $"""
                LLM synthesis failed: {ex.Message}

                Falling back to structured results:

                {xrayOutput}
                """;
        }
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
