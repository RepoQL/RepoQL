using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Explore.Search;
using CoreTokenEstimator = RepoQL.Contracts.TokenEstimator;

namespace RepoQL.Explore;

/// <summary>
/// Orchestrates explore search and rendering operations.
/// </summary>
public sealed class ExploreOrchestrator
{
    private readonly IExploreSearchEngine _searchEngine;
    private readonly IJitObjectSearchService? _jitService;
    private readonly ILlmProvider? _llmProvider;
    private readonly IInspectRefinementService? _inspectRefinementService;
    private readonly InspectRefinementOptions _inspectRefinementOptions;

    /// <summary>
    /// Minimum token budget for Understand intent to ensure sufficient context for LLM synthesis.
    /// </summary>
    private const int UnderstandMinBudget = 3000;

    public ExploreOrchestrator(
        IExploreSearchEngine searchEngine,
        IJitObjectSearchService? jitService = null,
        ILlmProvider? llmProvider = null,
        IInspectRefinementService? inspectRefinementService = null,
        InspectRefinementOptions? inspectRefinementOptions = null)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _jitService = jitService;
        _llmProvider = llmProvider;
        _inspectRefinementService = inspectRefinementService;
        _inspectRefinementOptions = inspectRefinementOptions ?? new InspectRefinementOptions();
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
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch = null)
    {
        if (query.TokenBudget <= 0)
            throw new ArgumentException("tokenBudget must be a positive integer.", nameof(query));

        // Handle Understand intent: check LLM provider and auto-scale budget
        var isUnderstand = query.Intent == Intent.Explain;
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
                return new ExploreExecutionResult(errorMsg, [], Truncated: false);
            }

            if (string.IsNullOrWhiteSpace(query.Keywords))
            {
                var errorMsg = """
                    Keywords required for 'understand' intent. The keywords become the question for LLM synthesis.

                    Example: explore(tokenBudget=2000, intent=understand, keywords="How does authentication work?")
                    """;
                return new ExploreExecutionResult(errorMsg, [], Truncated: false);
            }
        }

        // Auto-scale budget for Understand intent
        var effectiveBudget = isUnderstand
            ? Math.Max(query.TokenBudget, UnderstandMinBudget)
            : query.TokenBudget;

        // For Understand, we use Find behavior internally for search/allocation
        var searchIntent = isUnderstand ? Intent.Locate : query.Intent;

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
            return new ExploreExecutionResult(emptyOutput, [], Truncated: false);
        }

        // Convert SearchResult → ExploreResult
        var exploreResults = searchResult.Results.Select(ToExploreResult).ToList();

        // De-duplicate: remove top-level results that already appear as children of other results
        exploreResults = DeduplicateResults(exploreResults);

        var hasSearchCriteria = !string.IsNullOrWhiteSpace(query.Keywords) || boostPatterns.Count > 0;
        var inspectRefinementEnabled = ShouldRunInspectRefinement(query);
        var refineBudget = inspectRefinementEnabled
            ? CalculateInspectRefineBudget(effectiveBudget, searchResult.Results)
            : 0;
        var breadthBudget = Math.Max(1, effectiveBudget - refineBudget);

        // Hierarchical token allocation (files compete first, then children within each file)
        var decisions = ValueBasedAllocator.Allocate(exploreResults, breadthBudget, searchIntent);

        // Apply limit if specified
        var limitedDecisions = query.Limit.HasValue && query.Limit.Value > 0
            ? decisions.Take(query.Limit.Value).ToList()
            : decisions;

        // Calculate omitted count for truncation info
        var omittedCount = exploreResults.Count - limitedDecisions.Count;
        var decisionResult = new DecisionResult(limitedDecisions, omittedCount, null);

        // Compose output (Inspect can optionally replace structure-heavy output with narrowed snippets)
        var renderedOutput = OutputComposer.Compose(decisionResult, hasSearchCriteria, status);
        var truncated = omittedCount > 0;

        if (inspectRefinementEnabled && refineBudget > 0)
        {
            var refined = await TryRenderInspectRefinementAsync(
                query,
                limitedDecisions,
                status,
                effectiveBudget,
                refineBudget,
                omittedCount,
                cancellationToken).ConfigureAwait(false);

            if (refined is not null)
            {
                renderedOutput = refined.Value.RenderedOutput;
                truncated = truncated || refined.Value.Truncated;
            }
        }

        // If Understand intent, synthesize via LLM
        if (isUnderstand && _llmProvider is not null)
        {
            // Re-render with LLM-friendly token budget (50k tokens ~= 200k chars for context)
            const int llmTokenBudget = 50_000;
            var llmDecisions = ValueBasedAllocator.Allocate(exploreResults, llmTokenBudget, Intent.Inspect);
            var llmDecisionResult = new DecisionResult(llmDecisions, 0, null);
            var llmOutput = OutputComposer.Compose(llmDecisionResult, hasSearchCriteria, status);

            var synthesized = await SynthesizeUnderstandingAsync(
                llmOutput,
                query.Keywords!,
                status,
                cancellationToken).ConfigureAwait(false);
            return new ExploreExecutionResult(synthesized, exploreResults, truncated);
        }

        return new ExploreExecutionResult(renderedOutput, exploreResults, truncated);
    }

    private bool ShouldRunInspectRefinement(ExploreQuery query)
    {
        if (!_inspectRefinementOptions.Enabled)
            return false;

        if (_inspectRefinementService is null)
            return false;

        if (query.Intent != Intent.Inspect)
            return false;

        return !string.IsNullOrWhiteSpace(query.Keywords);
    }

    private int CalculateInspectRefineBudget(int totalBudget, IReadOnlyList<SearchResult> rankedResults)
    {
        if (totalBudget < 900)
            return 0;

        var basePercent = Math.Clamp(
            _inspectRefinementOptions.BaseRefineBudgetPercent,
            _inspectRefinementOptions.MinRefineBudgetPercent,
            _inspectRefinementOptions.MaxRefineBudgetPercent);

        var adjustedPercent = basePercent;
        var topConfidence = rankedResults.Count > 0 ? rankedResults[0].Confidence : 0;
        var pivotIndex = Math.Min(3, rankedResults.Count - 1);
        var pivotConfidence = pivotIndex >= 0 ? rankedResults[pivotIndex].Confidence : topConfidence;
        var margin = topConfidence - pivotConfidence;

        // Lumpy rankings benefit from deeper narrowing; flat rankings preserve breadth.
        if (margin >= 18)
            adjustedPercent += 15;
        else if (margin <= 8)
            adjustedPercent -= 10;

        adjustedPercent = Math.Clamp(
            adjustedPercent,
            _inspectRefinementOptions.MinRefineBudgetPercent,
            _inspectRefinementOptions.MaxRefineBudgetPercent);

        var refineBudget = (int)Math.Round(totalBudget * adjustedPercent / 100.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(refineBudget, 0, Math.Max(0, totalBudget - 1));
    }

    private async Task<(string RenderedOutput, bool Truncated)?> TryRenderInspectRefinementAsync(
        ExploreQuery query,
        IReadOnlyList<RenderingDecision> limitedDecisions,
        IndexerStatus status,
        int totalBudget,
        int refineBudget,
        int omittedCount,
        CancellationToken cancellationToken)
    {
        if (_inspectRefinementService is null || string.IsNullOrWhiteSpace(query.Keywords))
            return null;

        var candidates = BuildRefinementCandidates(limitedDecisions);
        if (candidates.Count == 0)
            return null;

        var refinementResult = await _inspectRefinementService.RefineAsync(
            query.Keywords!,
            candidates,
            refineBudget,
            cancellationToken).ConfigureAwait(false);

        if (refinementResult.Results.Count == 0)
            return null;

        return ComposeInspectRefinedOutput(
            limitedDecisions,
            refinementResult,
            status,
            totalBudget,
            refineBudget,
            omittedCount);
    }

    private IReadOnlyList<InspectRefinementCandidate> BuildRefinementCandidates(IReadOnlyList<RenderingDecision> decisions)
    {
        var byUri = new Dictionary<string, InspectRefinementCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var decision in decisions)
        {
            var documentUri = ToDocumentUri(decision.Result.Uri);
            if (string.IsNullOrWhiteSpace(documentUri))
                continue;

            if (!byUri.TryGetValue(documentUri, out var existing) || decision.Result.Confidence > existing.Confidence)
            {
                byUri[documentUri] = new InspectRefinementCandidate(
                    Uri: documentUri,
                    Confidence: decision.Result.Confidence,
                    Headline: decision.Result.Headline,
                    Lang: decision.Result.Lang);
            }
        }

        return byUri.Values
            .OrderByDescending(c => c.Confidence)
            .Take(Math.Max(1, _inspectRefinementOptions.MaxDocumentsToRefine))
            .ToList();
    }

    private (string RenderedOutput, bool Truncated) ComposeInspectRefinedOutput(
        IReadOnlyList<RenderingDecision> decisions,
        InspectRefinementResult refinement,
        IndexerStatus status,
        int totalBudget,
        int refineBudget,
        int omittedCount)
    {
        var snippetsByDocument = refinement.Results
            .GroupBy(r => ToDocumentUri(r.Uri), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<InspectRefinedSnippet>)g
                    .OrderByDescending(s => s.Score)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>(decisions.Count * 2);
        var includedDocuments = 0;
        var truncatedByBudget = false;

        foreach (var decision in decisions)
        {
            var documentUri = ToDocumentUri(decision.Result.Uri);
            snippetsByDocument.TryGetValue(documentUri, out var snippets);
            var maxSnippets = SnippetsPerDocument(decision.Result.Confidence);

            var block = BuildInspectBlock(decision, snippets, maxSnippets);
            var tentative = string.Join("\n\n", lines.Append(block));
            var hint = BuildInspectRefinementHint(refinement, refineBudget, totalBudget);
            var footer = RepresentationFormatter.FormatStatusFooter(
                status,
                CoreTokenEstimator.EstimateTokens(tentative),
                hint);
            var candidateOutput = $"{tentative}\n\n{footer}";
            var candidateTokens = CoreTokenEstimator.EstimateTokens(candidateOutput);

            if (includedDocuments > 0 && candidateTokens > totalBudget)
            {
                truncatedByBudget = true;
                break;
            }

            lines.Add(block);
            includedDocuments++;
        }

        var content = lines.Count == 0
            ? BuildInspectBlock(decisions[0], snippetsByDocument.GetValueOrDefault(ToDocumentUri(decisions[0].Result.Uri)), SnippetsPerDocument(decisions[0].Result.Confidence))
            : string.Join("\n\n", lines);

        var hintText = BuildInspectRefinementHint(refinement, refineBudget, totalBudget);
        var tokenCount = CoreTokenEstimator.EstimateTokens(content);
        var footerText = RepresentationFormatter.FormatStatusFooter(status, tokenCount, hintText);
        var rendered = $"{content}\n\n{footerText}";
        var finalTruncated = truncatedByBudget || omittedCount > 0 || includedDocuments < decisions.Count;
        return (rendered, finalTruncated);
    }

    private int SnippetsPerDocument(int confidence)
    {
        if (confidence >= _inspectRefinementOptions.HighConfidenceThreshold)
            return Math.Max(1, _inspectRefinementOptions.HighConfidenceSnippetsPerDocument);

        if (confidence >= _inspectRefinementOptions.MediumConfidenceThreshold)
            return Math.Max(1, _inspectRefinementOptions.MediumConfidenceSnippetsPerDocument);

        return Math.Max(1, _inspectRefinementOptions.LowConfidenceSnippetsPerDocument);
    }

    private static string BuildInspectBlock(
        RenderingDecision decision,
        IReadOnlyList<InspectRefinedSnippet>? snippets,
        int snippetLimit)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        builder.Append(decision.Result.Confidence.ToString(CultureInfo.InvariantCulture).PadLeft(3));
        builder.Append("%] ");
        builder.Append(decision.Result.Uri);

        var headline = decision.Result.Headline;
        if (!string.IsNullOrWhiteSpace(headline))
        {
            builder.Append('\n');
            builder.Append("  ");
            builder.Append(headline);
        }

        if (snippets is null || snippets.Count == 0 || snippetLimit <= 0)
            return builder.ToString();

        foreach (var snippet in snippets.Take(snippetLimit))
        {
            if (string.IsNullOrWhiteSpace(snippet.Snippet))
                continue;

            var fragment = BuildLineFragment(snippet.LineStart, snippet.LineEnd);
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                builder.Append('\n');
                builder.Append("  ");
                builder.Append(snippet.Uri);
                builder.Append(fragment);
                builder.Append("  [score: ");
                builder.Append(snippet.Score.ToString("F2", CultureInfo.InvariantCulture));
                builder.Append(']');
            }

            builder.Append('\n');
            builder.Append("  ```");
            if (!string.IsNullOrWhiteSpace(snippet.Lang))
                builder.Append(snippet.Lang);
            builder.Append('\n');
            builder.Append(snippet.Snippet.TrimEnd());
            builder.Append("\n  ```");
        }

        return builder.ToString();
    }

    private static string BuildLineFragment(int? lineStart, int? lineEnd)
    {
        if (!lineStart.HasValue)
            return string.Empty;

        if (!lineEnd.HasValue || lineEnd.Value == lineStart.Value)
            return $"#line={lineStart.Value}";

        return $"#line={lineStart.Value},{lineEnd.Value}";
    }

    private static string BuildInspectRefinementHint(
        InspectRefinementResult refinement,
        int refineBudget,
        int totalBudget)
    {
        var parts = new List<string>
        {
            "showing: inspect refined",
            $"refine_budget: {Math.Clamp((int)Math.Round(refineBudget * 100.0 / Math.Max(1, totalBudget)), 0, 100)}%"
        };

        parts.Add($"adaptive: rounds={refinement.Rounds}, widenings={refinement.Widenings}, cap={refinement.FinalCandidateLimit}");

        if (refinement.FallbackUsed)
            parts.Add("fallback: used");

        if (refinement.TimedOut)
            parts.Add("refine: timeout");

        if (!string.IsNullOrWhiteSpace(refinement.DegradedReason))
            parts.Add($"degraded: {refinement.DegradedReason}");

        return string.Join(" | ", parts);
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
    /// Synthesize understanding from explore output using LLM.
    /// </summary>
    private async Task<string> SynthesizeUnderstandingAsync(
        string exploreOutput,
        string question,
        IndexerStatus status,
        CancellationToken ct)
    {
        // The explore output becomes the context, the keywords become the question
        // System prompt (CoreSystemPrompt with capsules) handles format and wisdom
        var intent = question;

        try
        {
            var result = await _llmProvider!.SummarizeAsync(
                exploreOutput,
                intent,
                maxTokens: 1000,
                repoTree: null,
                ct: ct).ConfigureAwait(false);

            // Calculate token count for the response content (excluding footer)
            var responseContent = $"## Understanding: {question}\n\n{result}";
            var tokenCount = CoreTokenEstimator.EstimateTokens(responseContent);
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

                {exploreOutput}
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
            ChildObjects: childObjects
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
