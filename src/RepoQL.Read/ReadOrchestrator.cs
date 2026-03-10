using System.Diagnostics;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Contracts.Inference;
using CoreTokenEstimator = RepoQL.Contracts.TokenEstimator;

using RepoQL.Explore;

namespace RepoQL.Read;

/// <summary>
/// Orchestrates read operations with token-budget-aware representation selection.
///
/// Purpose: Provides a server-side implementation for reading repository content
/// that automatically selects the most appropriate representation level (full content,
/// structure, or headline) based on available token budget. Supports both direct content
/// fetch and LLM-powered synthesis via ExploreOrchestrator's balanced breadth mode.
///
/// Complexity: Integrates with IReadContentProvider for content fetching, TokenEstimator
/// for budget decisions, ExploreOrchestrator for large content question synthesis, and
/// IInferenceProvider for direct inference calls on smaller content. The progressive disclosure
/// logic (full -> structure -> headline) and glob handling are encapsulated here.
/// </summary>
public sealed partial class ReadOrchestrator
{
    private readonly IReadContentProvider _contentProvider;
    private readonly ExploreOrchestrator _exploreOrchestrator;
    private readonly IInferenceProvider? _inferenceProvider;
    private readonly ModifierDispatcher _modifierDispatcher;

    /// <summary>
    /// Token threshold above which we use explore Understand pipeline instead of direct LLM call.
    /// 100k tokens ~= 400k chars. Beyond this, explore's search + synthesis is more effective.
    /// </summary>
    private const int LargeContentThreshold = 100_000;

    public ReadOrchestrator(
        IReadContentProvider contentProvider,
        ExploreOrchestrator exploreOrchestrator,
        IInferenceProvider? inferenceProvider = null,
        IEnumerable<IModifierHandler>? modifierHandlers = null)
    {
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
        _exploreOrchestrator = exploreOrchestrator ?? throw new ArgumentNullException(nameof(exploreOrchestrator));
        _inferenceProvider = inferenceProvider;
        _modifierDispatcher = new ModifierDispatcher(_contentProvider, modifierHandlers);
    }

    /// <summary>
    /// Execute a read operation and return rendered output.
    /// </summary>
    /// <param name="uri">URI or glob pattern. Modifier syntax is handled by the dispatcher.</param>
    /// <param name="tokenBudget">Token budget for representation selection.</param>
    /// <param name="status">Current indexer status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="stopwatch">Optional stopwatch for timing.</param>
    public async Task<ReadExecutionResult> ExecuteAsync(
        string uri,
        int tokenBudget,
        TrustSignal status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch = null)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return new ReadExecutionResult(Success: false, Error: "URI cannot be empty.");

        if (tokenBudget <= 0)
            return new ReadExecutionResult(Success: false, Error: "tokenBudget must be a positive integer.");

        var trimmedUri = uri.Trim();

        // Check for question syntax before modifier dispatch
        // Question syntax: "pattern => question: How does X work?"
        if (TryParseQuestion(trimmedUri, out var pattern, out var question))
        {
            return await ExecuteWithQuestionAsync(
                pattern!, question!, tokenBudget, status, cancellationToken, stopwatch)
                .ConfigureAwait(false);
        }

        var modifierResult = await _modifierDispatcher
            .TryExecuteAsync(trimmedUri, tokenBudget, status, cancellationToken, stopwatch)
            .ConfigureAwait(false);
        if (modifierResult is not null)
            return modifierResult;

        return await ExecuteDirectAsync(trimmedUri, tokenBudget, status, cancellationToken, stopwatch).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse question syntax from input. Format: "pattern => question: text"
    /// </summary>
    /// <param name="input">The input string to parse.</param>
    /// <param name="pattern">Output: the URI pattern before the separator.</param>
    /// <param name="question">Output: the question text after "question:".</param>
    /// <returns>True if valid question syntax was found, false otherwise.</returns>
    private static bool TryParseQuestion(string input, out string? pattern, out string? question)
    {
        pattern = null;
        question = null;

        var separatorIndex = input.IndexOf("=>", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return false;

        var patternPart = input[..separatorIndex].Trim();
        var remainder = input[(separatorIndex + 2)..].Trim();

        // Check for "question:" prefix (case-insensitive)
        if (!remainder.StartsWith("question:", StringComparison.OrdinalIgnoreCase))
            return false;

        var questionPart = remainder[9..].Trim(); // "question:".Length == 9

        if (string.IsNullOrWhiteSpace(patternPart) || string.IsNullOrWhiteSpace(questionPart))
            return false;

        pattern = patternPart;
        question = questionPart;
        return true;
    }

    /// <summary>
    /// Execute tree format for matching files with progressive disclosure.
    /// 1. Try full tree (with files)
    /// 2. If over budget, try folders-only tree with type counts
    /// 3. If still over budget, show info message
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteTreeAsync(
        string globPattern,
        int tokenBudget,
        TrustSignal status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        try
        {
            // Fetch matching documents (we only need URIs, but use existing method)
            var documents = await _contentProvider.FetchGlobAsync(globPattern, cancellationToken).ConfigureAwait(false);

            if (documents.Count == 0)
            {
                var diagnostic = await NoMatchDiagnostics.DiagnoseAsync(globPattern, _contentProvider, status, cancellationToken).ConfigureAwait(false);
                return new ReadExecutionResult(
                    Success: true,
                    RenderedOutput: diagnostic,
                    Representation: "tree",
                    FilesRead: 0,
                    FilesOmitted: 0);
            }

            var uris = documents.Select(d => d.Uri).ToList();
            var statusWithTiming = status with { ExecutionTimeMs = stopwatch?.ElapsedMilliseconds ?? 0 };

            // Try full tree first
            var fullTree = await _contentProvider.FormatAsTreeAsync(uris, foldersOnly: false, includeHeadlines: true, cancellationToken).ConfigureAwait(false)
                           ?? string.Join("\n", uris);
            var fullTreeTokens = CoreTokenEstimator.EstimateTokens(fullTree);

            if (fullTreeTokens <= tokenBudget)
            {
                // Full tree fits - return it
                var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, fullTreeTokens);
                return new ReadExecutionResult(
                    Success: true,
                    RenderedOutput: $"{fullTree}\n{footer}",
                    Representation: "tree",
                    FilesRead: documents.Count,
                    FilesOmitted: 0);
            }

            // Full tree doesn't fit - try folders-only
            var foldersTree = await _contentProvider.FormatAsTreeAsync(uris, foldersOnly: true, includeHeadlines: false, cancellationToken).ConfigureAwait(false)
                              ?? "(folders-only not supported)";
            var foldersTreeTokens = CoreTokenEstimator.EstimateTokens(foldersTree);

            if (foldersTreeTokens <= tokenBudget)
            {
                // Folders-only fits - return with note about full tree size
                var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, foldersTreeTokens);
                var note = $"\n[Showing folders only ({foldersTreeTokens} tokens). Full tree with files: {fullTreeTokens} tokens]";
                return new ReadExecutionResult(
                    Success: true,
                    RenderedOutput: $"{foldersTree}{note}\n{footer}",
                    Representation: "tree-folders",
                    FilesRead: documents.Count,
                    FilesOmitted: 0);
            }

            // Neither fits - return with info message
            var budgetNeeded = Math.Min(fullTreeTokens, foldersTreeTokens);
            var exceedsMsg = $"""
                Tree output exceeds budget ({budgetNeeded} tokens needed, {tokenBudget} budget).

                To see the tree, increase tokenBudget to at least {budgetNeeded}.

                Full tree: {fullTreeTokens} tokens ({documents.Count} files)
                Folders only: {foldersTreeTokens} tokens
                """;

            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: exceedsMsg,
                Representation: "tree-exceeded",
                FilesRead: 0,
                FilesOmitted: documents.Count);
        }
        catch (Exception ex)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"Error generating tree for {globPattern}: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute direct read without LLM synthesis. Applies progressive disclosure based on budget.
    /// Uses matches_glob for all URIs - handles exact URIs, globs, and fragment patterns uniformly.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteDirectAsync(
        string globUri,
        int tokenBudget,
        TrustSignal status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        var documents = await _contentProvider.FetchGlobAsync(globUri, cancellationToken).ConfigureAwait(false);

        if (documents.Count == 0)
        {
            var diagnostic = await NoMatchDiagnostics.DiagnoseAsync(globUri, _contentProvider, status, cancellationToken).ConfigureAwait(false);
            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: diagnostic,
                Representation: "glob",
                FilesRead: 0,
                FilesOmitted: 0);
        }

        var sb = new StringBuilder();
        var remainingBudget = tokenBudget;
        var filesIncluded = 0;
        var filesOmitted = 0;

        // Track representation levels for summary
        var levelCounts = new Dictionary<string, int>();
        var maxFullCost = 0;

        foreach (var doc in documents)
        {
            // Calculate per-file budget share
            var fileBudget = remainingBudget / Math.Max(1, documents.Count - filesIncluded);
            var (fileOutput, level, costs) = SelectRepresentation(doc, fileBudget);

            var fileTokens = CoreTokenEstimator.EstimateTokens(fileOutput);

            // Include file if it fits or if it's the first one
            if (fileTokens <= remainingBudget || filesIncluded == 0)
            {
                // For single result, render without separator
                if (documents.Count == 1)
                {
                    sb.Append(fileOutput);
                }
                // For headlines, compact format: uri | headline (one line each, no blank lines)
                // Strip filename from headline since URI already contains it
                else if (level == "headline")
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(doc.Uri);
                    sb.Append(" | ");
                    sb.Append(StripFilenameFromHeadline(fileOutput));
                }
                // For structure/full content, use separators
                else
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.AppendLine($"--- {doc.Uri} ---");
                    sb.Append(fileOutput);
                }
                remainingBudget -= fileTokens;
                filesIncluded++;

                // Track representation level
                levelCounts[level] = levelCounts.GetValueOrDefault(level) + 1;

                // Track max full cost for files not at full representation
                if (level != "full" && costs.FullTokens.HasValue)
                    maxFullCost = Math.Max(maxFullCost, costs.FullTokens.Value);
            }
            else
            {
                filesOmitted++;
            }
        }

        if (filesOmitted > 0)
        {
            sb.Append($"\n\n[{filesOmitted} more files omitted - increase tokenBudget to see more]");
        }

        var output = sb.ToString();
        var tokens = CoreTokenEstimator.EstimateTokens(output);
        var statusWithTiming = status with { ExecutionTimeMs = stopwatch?.ElapsedMilliseconds ?? 0 };

        // For single result, use single-file formatting; for multiple, use glob formatting
        string? hint;
        string representation;
        if (documents.Count == 1 && filesIncluded == 1)
        {
            var singleLevel = levelCounts.Keys.First();
            var singleCosts = new RepresentationCosts(
                maxFullCost > 0 ? maxFullCost : null,
                levelCounts.ContainsKey("structure") ? tokens : null,
                levelCounts.ContainsKey("headline") ? tokens : null);
            hint = RepresentationFormatter.FormatRepresentationHint(singleLevel, singleCosts);
            representation = singleLevel;
        }
        else
        {
            hint = FormatGlobRepresentationHint(levelCounts, maxFullCost);
            representation = "glob";
        }

        var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, tokens, hint);

        return new ReadExecutionResult(
            Success: true,
            RenderedOutput: $"{output}\n{footer}",
            Representation: representation,
            FilesRead: filesIncluded,
            FilesOmitted: filesOmitted);
    }

    /// <summary>
    /// Format a representation hint for glob results showing level breakdown.
    /// Returns inner content (without brackets) or null if all files are at full representation.
    /// </summary>
    private static string? FormatGlobRepresentationHint(Dictionary<string, int> levelCounts, int maxFullCost)
    {
        // If all files are at full representation, no hint needed
        if (levelCounts.Count == 1 && levelCounts.ContainsKey("full"))
            return null;

        var parts = new List<string>();

        // Show level breakdown
        var breakdown = levelCounts
            .OrderByDescending(kvp => kvp.Key == "full" ? 3 : kvp.Key == "structure" ? 2 : kvp.Key == "headline" ? 1 : 0)
            .Select(kvp => $"{kvp.Value}x {kvp.Key}");
        parts.Add($"representations: {string.Join(", ", breakdown)}");

        // Show max full cost if any file is not at full representation
        if (maxFullCost > 0)
            parts.Add($"largest file full: {maxFullCost} tok");

        // Return inner content without brackets - caller will integrate into footer
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Execute read with LLM synthesis. For small content (&lt; 100k tokens), calls LLM directly.
    /// For large content, delegates to ExploreOrchestrator's Understand pipeline which uses
    /// search + synthesis to find relevant sections.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteWithQuestionAsync(
        string uri,
        string question,
        int tokenBudget,
        TrustSignal status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        if (_inferenceProvider is null || !_inferenceProvider.Available)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: "Inference service not configured. Set inference.service_url and cloud.api_key to enable question synthesis.");
        }

        // Fetch content - matches_glob handles exact URIs, globs, and fragment patterns uniformly
        var documents = await _contentProvider.FetchGlobAsync(uri, cancellationToken).ConfigureAwait(false);

        if (documents.Count == 0)
        {
            var diagnostic = await NoMatchDiagnostics.DiagnoseAsync(uri, _contentProvider, status, cancellationToken).ConfigureAwait(false);
            return new ReadExecutionResult(
                Success: false,
                Error: diagnostic);
        }

        // Calculate total content size
        var allContent = new StringBuilder();
        foreach (var doc in documents)
        {
            if (!string.IsNullOrEmpty(doc.TextContent))
            {
                if (allContent.Length > 0) allContent.Append("\n\n--- ").Append(doc.Uri).Append(" ---\n");
                allContent.Append(doc.TextContent);
            }
        }

        var contentTokens = CoreTokenEstimator.EstimateTokens(allContent.ToString());

        // If content is small enough, call LLM directly
        if (contentTokens < LargeContentThreshold)
        {
            return await ExecuteDirectLlmAsync(
                documents, allContent.ToString(), question, tokenBudget, status, stopwatch, cancellationToken).ConfigureAwait(false);
        }

        // Large content: delegate to explore's Understand pipeline for search + synthesis
        return await ExecuteExploreExplainAsync(uri, question, tokenBudget, status, stopwatch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Call LLM directly with the content and question when content is small enough.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteDirectLlmAsync(
        IReadOnlyList<ReadDocument> documents,
        string content,
        string question,
        int tokenBudget,
        TrustSignal status,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        // Build context with file URIs for citation
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("Content from the following files:");
        foreach (var doc in documents)
        {
            contextBuilder.AppendLine($"- {doc.Uri}");
        }
        contextBuilder.AppendLine();
        contextBuilder.AppendLine(content);

        var contextWithFiles = contextBuilder.ToString();

        // Get repo tree for context (allows agent to suggest related files)
        // Budget is for LLM input context, not tool output — generous but bounded.
        const int repoTreeBudget = 10_000;
        var repoTree = await _contentProvider.GetRepoTreeAsync(scope: null, tokenBudget: repoTreeBudget, cancellationToken).ConfigureAwait(false);

        try
        {
            // Use the question as the intent, tokenBudget as maxTokens hint
            // Scale maxTokens: use 30% of budget for response (rest is for reasoning overhead)
            var maxResponseTokens = Math.Max(500, tokenBudget * 30 / 100);
            var fullContext = string.IsNullOrWhiteSpace(repoTree)
                ? contextWithFiles
                : $"{contextWithFiles}\n\nRepository tree:\n{repoTree}";
            var response = await _inferenceProvider!.CompleteAsync(
                new InferenceRequest
                {
                    Context = fullContext,
                    Prompt = question,
                    MaxTokens = maxResponseTokens
                },
                cancellationToken).ConfigureAwait(false);

            // Build output with status footer
            var tokens = CoreTokenEstimator.EstimateTokens(response.Content);
            var statusWithTiming = status with { ExecutionTimeMs = stopwatch?.ElapsedMilliseconds ?? 0 };
            var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, tokens);

            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: $"{response.Content}\n{footer}",
                Representation: "question",
                FilesRead: documents.Count,
                FilesOmitted: 0);
        }
        catch (Exception ex)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"Inference synthesis failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Delegate to explore's Understand pipeline for large content.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteExploreExplainAsync(
        string uri,
        string question,
        int tokenBudget,
        TrustSignal status,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        var query = new ExploreQuery(
            TokenBudget: tokenBudget,
            Breadth: 5,
            Scope: uri,
            Keywords: question,
            Boost: null,
            Penalize: null,
            Limit: null);

        var result = await _exploreOrchestrator.ExecuteAsync(query, status, cancellationToken, stopwatch).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.RenderedOutput))
        {
            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: result.RenderedOutput,
                Representation: "question",
                FilesRead: result.Results.Count,
                FilesOmitted: 0);
        }

        return new ReadExecutionResult(
            Success: false,
            Error: "No results from LLM synthesis.");
    }

    /// <summary>
    /// Select the richest representation that fits the token budget.
    /// Priority: full content -> headline + structure -> headline only
    /// Also returns token costs for all available representations.
    /// </summary>
    private static (string output, string level, RepresentationCosts costs) SelectRepresentation(ReadDocument doc, int budget)
    {
        // Calculate costs for all available representations
        int? fullTokens = null;
        int? structureTokens = null;
        int? headlineTokens = null;

        if (!string.IsNullOrEmpty(doc.TextContent))
            fullTokens = CoreTokenEstimator.EstimateTokens(doc.TextContent);

        if (!string.IsNullOrEmpty(doc.Headline) && !string.IsNullOrEmpty(doc.Structure))
        {
            var structureText = $"{doc.Headline}\n\n{doc.Structure}";
            structureTokens = CoreTokenEstimator.EstimateTokens(structureText);
        }

        if (!string.IsNullOrEmpty(doc.Headline))
            headlineTokens = CoreTokenEstimator.EstimateTokens(doc.Headline);

        var costs = new RepresentationCosts(fullTokens, structureTokens, headlineTokens);

        // Try full content first
        if (fullTokens.HasValue && fullTokens.Value <= budget)
            return (doc.TextContent!, "full", costs);

        // Try headline + structure
        if (structureTokens.HasValue && structureTokens.Value <= budget)
        {
            var structureText = $"{doc.Headline}\n\n{doc.Structure}";
            return (structureText, "structure", costs);
        }

        // Fall back to headline only
        if (!string.IsNullOrEmpty(doc.Headline))
            return (doc.Headline, "headline", costs);

        // Last resort: just indicate no content
        return ($"(No content available for {doc.Uri})", "none", costs);
    }

    /// <summary>
    /// Strips the filename prefix from a headline since the URI already contains it.
    /// Headlines are formatted as "filename | rest..." - returns "rest..." trimmed.
    /// </summary>
    private static string StripFilenameFromHeadline(string headline)
    {
        var separatorIndex = headline.IndexOf(" | ", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return headline.TrimEnd();

        return headline[(separatorIndex + 3)..].TrimEnd();
    }

}

/// <summary>
/// Result of a read execution.
/// </summary>
public sealed record ReadExecutionResult(
    bool Success,
    string? RenderedOutput = null,
    string? Error = null,
    string? Representation = null,
    int FilesRead = 0,
    int FilesOmitted = 0);


