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
        var renderedOutput = OutputComposer.Compose(decisionResult, hasSearchCriteria, status, searchIntent);
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

            if (refined is not null && !string.IsNullOrEmpty(refined.Value.RenderedOutput))
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
            var llmOutput = OutputComposer.Compose(llmDecisionResult, hasSearchCriteria, status, Intent.Inspect);

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
            omittedCount,
            query.Keywords!);
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
        int omittedCount,
        string keywords)
    {
        // Index decisions by document URI for short headline lookup
        var decisionByUri = new Dictionary<string, RenderingDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions)
            decisionByUri.TryAdd(ToDocumentUri(decision.Result.Uri), decision);

        // Deduplicate snippets: different URIs can resolve to the same physical file
        // (help://, file:///.claude/Skills/, file:///src/RepoQL.Documentation/).
        // Keep the highest-scored snippet per unique content.
        var deduped = refinement.Results
            .Where(s => !string.IsNullOrWhiteSpace(s.Snippet))
            .GroupBy(s => s.Snippet!.Trim(), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(s => s.Score).First())
            .ToList();

        // Generate variants for each snippet (full + peak-narrowed options).
        var allVariants = new List<SnippetVariant>();
        foreach (var snippet in deduped)
            allVariants.AddRange(GenerateVariants(snippet, keywords));

        // Multiple-choice knapsack: pick one variant per snippet to maximize value within budget.
        var variantsBySnippet = allVariants
            .GroupBy(v => v.SnippetKey)
            .Select(g => (
                Key: g.Key,
                BestRatio: g.Max(v => v.Value / Math.Max(1, v.Cost)),
                Variants: g.OrderByDescending(v => v.Cost).ToList()))
            .OrderByDescending(g => g.BestRatio)
            .ToList();

        var selectedByDocument = new Dictionary<string, List<(InspectRefinedSnippet Snippet, string Rendered)>>(
            StringComparer.OrdinalIgnoreCase);
        var snippetBudgetRemaining = refineBudget;

        foreach (var group in variantsBySnippet)
        {
            if (snippetBudgetRemaining <= 0)
                break;

            var chosen = group.Variants.FirstOrDefault(v => v.Cost <= snippetBudgetRemaining);

            if (chosen is null && selectedByDocument.Count == 0)
                chosen = group.Variants[^1]; // First snippet always gets included

            if (chosen is null)
                continue;

            var docUri = ToDocumentUri(chosen.Snippet.Uri);
            if (!selectedByDocument.TryGetValue(docUri, out var list))
            {
                list = [];
                selectedByDocument[docUri] = list;
            }

            list.Add((chosen.Snippet, chosen.Rendered));
            snippetBudgetRemaining -= chosen.Cost;
        }

        // Only render documents that have evidence. No headline-only blocks.
        var evidenceBlocks = new List<string>();
        var truncatedByBudget = false;

        var documentsWithEvidence = selectedByDocument
            .Select(kvp => (Uri: kvp.Key, BestScore: kvp.Value.Max(s => s.Snippet.Score), Snippets: kvp.Value))
            .OrderByDescending(d => d.BestScore)
            .ToList();

        foreach (var doc in documentsWithEvidence)
        {
            decisionByUri.TryGetValue(doc.Uri, out var decision);
            var block = BuildInspectEvidenceBlock(decision, doc.Uri, doc.Snippets);

            var tentative = string.Join("\n\n", evidenceBlocks.Append(block));
            var hint = BuildInspectRefinementHint(refinement, refineBudget, totalBudget);
            var footer = RepresentationFormatter.FormatStatusFooter(
                status,
                CoreTokenEstimator.EstimateTokens(tentative),
                hint);
            var candidateTokens = CoreTokenEstimator.EstimateTokens($"{tentative}\n\n{footer}");

            if (evidenceBlocks.Count > 0 && candidateTokens > totalBudget)
            {
                truncatedByBudget = true;
                break;
            }

            evidenceBlocks.Add(block);
        }

        if (evidenceBlocks.Count == 0)
            return (string.Empty, false); // Signal to caller: no evidence, use stage-1

        var content = string.Join("\n\n", evidenceBlocks);
        var hintText = BuildInspectRefinementHint(refinement, refineBudget, totalBudget);
        var tokenCount = CoreTokenEstimator.EstimateTokens(content);
        var footerText = RepresentationFormatter.FormatStatusFooter(status, tokenCount, hintText);
        var rendered = $"{content}\n\n{footerText}";
        var finalTruncated = truncatedByBudget || omittedCount > 0;
        return (rendered, finalTruncated);
    }

    private sealed record SnippetVariant(
        string SnippetKey,
        InspectRefinedSnippet Snippet,
        string Rendered,
        int Cost,
        double Value);

    private const int MinLinesForVariants = 6;
    private const double MediumValueFactor = 0.95;
    private const double TightValueFactor = 0.85;
    private const double PeakCoverageThreshold = 0.75;

    private static IReadOnlyList<SnippetVariant> GenerateVariants(
        InspectRefinedSnippet snippet,
        string keywords)
    {
        var key = $"{snippet.Uri}|{snippet.LineStart}|{snippet.LineEnd}";
        var fullRendered = RenderSnippetBlock(snippet);
        var fullCost = Math.Max(1, CoreTokenEstimator.EstimateTokens(fullRendered));
        var variants = new List<SnippetVariant>
        {
            new(key, snippet, fullRendered, fullCost, snippet.Score)
        };

        var lines = snippet.Snippet!.Split('\n');
        if (lines.Length < MinLinesForVariants)
            return variants;

        var peak = FindPeakLines(lines, keywords);
        if (peak is null)
            return variants;

        var (peakStart, peakEnd) = peak.Value;

        // Medium: peak ± 2 lines of context
        var medStart = Math.Max(0, peakStart - 2);
        var medEnd = Math.Min(lines.Length - 1, peakEnd + 2);
        if (medEnd - medStart + 1 < lines.Length)
        {
            var medSnippet = BuildVariantSnippet(snippet, lines, medStart, medEnd);
            var medRendered = RenderSnippetBlock(medSnippet);
            var medCost = Math.Max(1, CoreTokenEstimator.EstimateTokens(medRendered));
            variants.Add(new SnippetVariant(key, medSnippet, medRendered, medCost, snippet.Score * MediumValueFactor));
        }

        // Tight: peak ± 1 line of context
        var tightStart = Math.Max(0, peakStart - 1);
        var tightEnd = Math.Min(lines.Length - 1, peakEnd + 1);
        if (tightEnd - tightStart + 1 < medEnd - medStart + 1)
        {
            var tightSnippet = BuildVariantSnippet(snippet, lines, tightStart, tightEnd);
            var tightRendered = RenderSnippetBlock(tightSnippet);
            var tightCost = Math.Max(1, CoreTokenEstimator.EstimateTokens(tightRendered));
            variants.Add(new SnippetVariant(key, tightSnippet, tightRendered, tightCost, snippet.Score * TightValueFactor));
        }

        return variants;
    }

    /// <summary>
    /// Find the contiguous region of keyword-matching lines within a snippet.
    /// Returns null if no keywords match or if the peak spans most of the snippet.
    /// </summary>
    private static (int Start, int End)? FindPeakLines(string[] lines, string keywords)
    {
        var terms = keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return null;

        var matchingLines = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsStructuralNoise(line))
                continue;

            foreach (var term in terms)
            {
                if (term.Length >= 3 && line.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matchingLines.Add(i);
                    break;
                }
            }
        }

        if (matchingLines.Count == 0)
            return null;

        var peakStart = matchingLines[0];
        var peakEnd = matchingLines[^1];

        // If peak spans most of the snippet, there's no useful narrowing
        if ((peakEnd - peakStart + 1) > lines.Length * PeakCoverageThreshold)
            return null;

        return (peakStart, peakEnd);
    }

    /// <summary>
    /// A line is structural noise if it carries no semantic content worth anchoring a peak on.
    /// Intentionally minimal: only filters lines that could never be evidence regardless of query.
    /// </summary>
    private static bool IsStructuralNoise(string line)
    {
        var trimmed = line.Trim();
        // Empty or whitespace-only
        if (trimmed.Length == 0) return true;
        // Too short to carry meaning: single braces, brackets, parens, semicolons
        if (trimmed.Length <= 2) return true;
        return false;
    }

    private static InspectRefinedSnippet BuildVariantSnippet(
        InspectRefinedSnippet original,
        string[] allLines,
        int variantStart,
        int variantEnd)
    {
        var narrowedText = string.Join('\n',
            allLines[variantStart..(variantEnd + 1)]
                .Select(l => l.TrimEnd('\r')));

        // Compute the file line numbers for the variant.
        // The snippet text may include context lines beyond the zoom's LineStart..LineEnd.
        int? newLineStart = null, newLineEnd = null;
        if (original.LineStart.HasValue && original.LineEnd.HasValue)
        {
            var coreLines = original.LineEnd.Value - original.LineStart.Value + 1;
            var contextBefore = Math.Max(0, (allLines.Length - coreLines) / 2);
            var snippetFileStart = original.LineStart.Value - contextBefore;

            newLineStart = snippetFileStart + variantStart;
            newLineEnd = snippetFileStart + variantEnd;
        }

        return original with
        {
            Snippet = narrowedText,
            LineStart = newLineStart,
            LineEnd = newLineEnd
        };
    }

    /// <summary>Render a single snippet's code block (without document header). Used for cost estimation.</summary>
    private static string RenderSnippetBlock(InspectRefinedSnippet snippet)
    {
        var builder = new StringBuilder();
        var fragment = BuildLineFragment(snippet.LineStart, snippet.LineEnd);
        if (!string.IsNullOrWhiteSpace(fragment))
        {
            builder.Append("  ");
            builder.Append(snippet.Uri);
            builder.Append(fragment);
            builder.Append("  [score: ");
            builder.Append(snippet.Score.ToString("F2", CultureInfo.InvariantCulture));
            builder.Append("]\n");
        }

        builder.Append("  ```");
        if (!string.IsNullOrWhiteSpace(snippet.Lang))
            builder.Append(snippet.Lang);
        builder.Append('\n');
        builder.Append(snippet.Snippet!.TrimEnd());
        builder.Append("\n  ```");
        return builder.ToString();
    }

    /// <summary>
    /// Build an evidence-first block for Inspect output.
    /// Short headline + code snippets. No Inventory-style metadata.
    /// </summary>
    private static string BuildInspectEvidenceBlock(
        RenderingDecision? decision,
        string documentUri,
        IReadOnlyList<(InspectRefinedSnippet Snippet, string Rendered)> snippets)
    {
        var builder = new StringBuilder();

        if (decision is not null)
        {
            builder.Append('[');
            builder.Append(decision.Result.Confidence.ToString(CultureInfo.InvariantCulture).PadLeft(3));
            builder.Append("%] ");
            builder.Append(decision.Result.Uri);

            var headline = RepresentationFormatter.ShortHeadline(decision.Result.Headline);
            if (!string.IsNullOrWhiteSpace(headline))
            {
                builder.Append('\n');
                builder.Append("  ");
                builder.Append(headline);
            }
        }
        else
        {
            builder.Append(documentUri);
        }

        foreach (var (_, rendered) in snippets)
        {
            builder.Append('\n');
            builder.Append(rendered);
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
        var parts = new List<string>();

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
