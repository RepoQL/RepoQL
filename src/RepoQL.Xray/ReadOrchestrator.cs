using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using CoreTokenEstimator = RepoQL.Contracts.TokenEstimator;

namespace RepoQL.Xray;

/// <summary>
/// Orchestrates read operations with token-budget-aware representation selection.
///
/// Purpose: Provides a server-side implementation for reading repository content
/// that automatically selects the most appropriate representation level (full content,
/// structure, or headline) based on available token budget. Supports both direct content
/// fetch and LLM-powered synthesis via XrayOrchestrator's Understand intent.
///
/// Complexity: Integrates with IReadContentProvider for content fetching, TokenEstimator
/// for budget decisions, XrayOrchestrator for large content question synthesis, and
/// ILlmProvider for direct LLM calls on smaller content. The progressive disclosure
/// logic (full -> structure -> headline) and glob handling are encapsulated here.
/// </summary>
public sealed partial class ReadOrchestrator
{
    private readonly IReadContentProvider _contentProvider;
    private readonly XrayOrchestrator _xrayOrchestrator;
    private readonly ILlmProvider? _llmProvider;

    /// <summary>
    /// Token threshold above which we use xray Understand pipeline instead of direct LLM call.
    /// 100k tokens ~= 400k chars. Beyond this, xray's search + synthesis is more effective.
    /// </summary>
    private const int LargeContentThreshold = 100_000;

    public ReadOrchestrator(
        IReadContentProvider contentProvider,
        XrayOrchestrator xrayOrchestrator,
        ILlmProvider? llmProvider = null)
    {
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
        _xrayOrchestrator = xrayOrchestrator ?? throw new ArgumentNullException(nameof(xrayOrchestrator));
        _llmProvider = llmProvider;
    }

    /// <summary>
    /// Execute a read operation and return rendered output.
    /// </summary>
    /// <param name="uri">URI or glob pattern. May contain ' // question' for LLM synthesis or ' => tree' for tree format.</param>
    /// <param name="tokenBudget">Token budget for representation selection.</param>
    /// <param name="status">Current indexer status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="stopwatch">Optional stopwatch for timing.</param>
    public async Task<ReadExecutionResult> ExecuteAsync(
        string uri,
        int tokenBudget,
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch = null)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return new ReadExecutionResult(Success: false, Error: "URI cannot be empty.");

        if (tokenBudget <= 0)
            return new ReadExecutionResult(Success: false, Error: "tokenBudget must be a positive integer.");

        var trimmedUri = uri.Trim();

        // Check for tree syntax: <glob> => tree
        var treeMatch = TreePattern().Match(trimmedUri);
        if (treeMatch.Success)
        {
            var globPattern = treeMatch.Groups[1].Value.Trim();
            return await ExecuteTreeAsync(globPattern, tokenBudget, status, cancellationToken, stopwatch).ConfigureAwait(false);
        }

        // Check for question syntax: <uri> // <question>
        var questionMatch = QuestionPattern().Match(trimmedUri);
        if (questionMatch.Success)
        {
            var targetUri = questionMatch.Groups[1].Value;
            var question = questionMatch.Groups[2].Value;
            return await ExecuteWithQuestionAsync(targetUri, question, tokenBudget, status, cancellationToken, stopwatch).ConfigureAwait(false);
        }

        return await ExecuteDirectAsync(trimmedUri, tokenBudget, status, cancellationToken, stopwatch).ConfigureAwait(false);
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
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        try
        {
            // Fetch matching documents (we only need URIs, but use existing method)
            var documents = await _contentProvider.FetchGlobAsync(globPattern, cancellationToken).ConfigureAwait(false);

            if (documents.Count == 0)
            {
                return new ReadExecutionResult(
                    Success: true,
                    RenderedOutput: $"No files matched: {globPattern}",
                    Representation: "tree",
                    FilesRead: 0,
                    FilesOmitted: 0);
            }

            var uris = documents.Select(d => d.Uri).ToList();
            var statusWithTiming = status with { ElapsedMs = stopwatch?.ElapsedMilliseconds ?? 0 };

            // Try full tree first
            var fullTree = await _contentProvider.FormatAsTreeAsync(uris, foldersOnly: false, cancellationToken).ConfigureAwait(false)
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
            var foldersTree = await _contentProvider.FormatAsTreeAsync(uris, foldersOnly: true, cancellationToken).ConfigureAwait(false)
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
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteDirectAsync(
        string uri,
        int tokenBudget,
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        try
        {
            // Handle glob patterns
            if (IsGlobPattern(uri))
            {
                return await ExecuteGlobAsync(uri, tokenBudget, status, cancellationToken, stopwatch).ConfigureAwait(false);
            }

            // Single file/resource
            return await ExecuteSingleAsync(uri, tokenBudget, status, cancellationToken, stopwatch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"Error reading {uri}: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute read for a single resource with progressive disclosure.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteSingleAsync(
        string uri,
        int tokenBudget,
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        var document = await _contentProvider.FetchDocumentAsync(uri, cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"No content found for: {uri}");
        }

        var (output, level, costs) = SelectRepresentation(document, tokenBudget);

        // Build output with status footer (including representation hint if not full)
        var tokens = CoreTokenEstimator.EstimateTokens(output);
        var statusWithTiming = status with { ElapsedMs = stopwatch?.ElapsedMilliseconds ?? 0 };
        var hint = RepresentationFormatter.FormatRepresentationHint(level, costs);
        var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, tokens, hint);

        return new ReadExecutionResult(
            Success: true,
            RenderedOutput: $"{output}\n{footer}",
            Representation: level,
            FilesRead: 1,
            FilesOmitted: 0);
    }

    /// <summary>
    /// Execute read for multiple files matching a glob pattern, distributing budget across matches.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteGlobAsync(
        string globUri,
        int tokenBudget,
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        var documents = await _contentProvider.FetchGlobAsync(globUri, cancellationToken).ConfigureAwait(false);

        if (documents.Count == 0)
        {
            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: $"No files matched: {globUri}",
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
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append($"--- {doc.Uri} ---\n");
                sb.Append(fileOutput);
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
        var statusWithTiming = status with { ElapsedMs = stopwatch?.ElapsedMilliseconds ?? 0 };

        // Add representation summary if not all files are at full representation
        var hint = FormatGlobRepresentationHint(levelCounts, maxFullCost);
        var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, tokens, hint);

        return new ReadExecutionResult(
            Success: true,
            RenderedOutput: $"{output}\n{footer}",
            Representation: "glob",
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
    /// For large content, delegates to XrayOrchestrator's Understand pipeline which uses
    /// search + synthesis to find relevant sections.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteWithQuestionAsync(
        string uri,
        string question,
        int tokenBudget,
        IndexerStatus status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch)
    {
        // First, fetch the content
        IReadOnlyList<ReadDocument> documents;
        if (IsGlobPattern(uri))
        {
            documents = await _contentProvider.FetchGlobAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var doc = await _contentProvider.FetchDocumentAsync(uri, cancellationToken).ConfigureAwait(false);
            documents = doc is not null ? [doc] : [];
        }

        if (documents.Count == 0)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"No content found for: {uri}");
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

        // If content is small enough and LLM is available, call directly
        if (contentTokens < LargeContentThreshold && _llmProvider is { Enabled: true })
        {
            return await ExecuteDirectLlmAsync(
                documents, allContent.ToString(), question, tokenBudget, status, stopwatch, cancellationToken).ConfigureAwait(false);
        }

        // Large content or no LLM: delegate to xray's Understand pipeline
        return await ExecuteXrayUnderstandAsync(uri, question, tokenBudget, status, stopwatch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Call LLM directly with the content and question when content is small enough.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteDirectLlmAsync(
        IReadOnlyList<ReadDocument> documents,
        string content,
        string question,
        int tokenBudget,
        IndexerStatus status,
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
        var repoTree = await _contentProvider.GetRepoTreeAsync(scope: null, cancellationToken).ConfigureAwait(false);

        try
        {
            // Use the question as the intent, tokenBudget as maxTokens hint
            // Scale maxTokens: use 30% of budget for response (rest is for reasoning overhead)
            var maxResponseTokens = Math.Max(500, tokenBudget * 30 / 100);
            var response = await _llmProvider!.SummarizeAsync(
                contextWithFiles,
                question,
                maxTokens: maxResponseTokens,
                repoTree: repoTree,
                ct: cancellationToken).ConfigureAwait(false);

            // Build output with status footer
            var tokens = CoreTokenEstimator.EstimateTokens(response);
            var statusWithTiming = status with { ElapsedMs = stopwatch?.ElapsedMilliseconds ?? 0 };
            var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, tokens);

            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: $"{response}\n{footer}",
                Representation: "question",
                FilesRead: documents.Count,
                FilesOmitted: 0);
        }
        catch (Exception ex)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"LLM synthesis failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Delegate to xray's Understand pipeline for large content.
    /// </summary>
    private async Task<ReadExecutionResult> ExecuteXrayUnderstandAsync(
        string uri,
        string question,
        int tokenBudget,
        IndexerStatus status,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        var query = new XrayQuery(
            TokenBudget: tokenBudget,
            Intent: Intent.Understand,
            Scope: uri,
            Keywords: question,
            Boost: null,
            Penalize: null,
            Limit: null);

        var result = await _xrayOrchestrator.ExecuteAsync(query, status, cancellationToken, stopwatch).ConfigureAwait(false);

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

    private static bool IsGlobPattern(string uri)
        => uri.Contains('*') || uri.Contains('?') || uri.Contains(';') || uri.Contains('!');

    [GeneratedRegex(@"^(\S+)\s+//\s+(.+)$")]
    private static partial Regex QuestionPattern();

    [GeneratedRegex(@"^(.+?)\s+=>\s*tree\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TreePattern();
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

/// <summary>
/// Document data for read operations.
/// </summary>
public sealed record ReadDocument(
    string Uri,
    string? TextContent,
    string? MediaType,
    string? Headline,
    string? Summary,
    string? Structure);

/// <summary>
/// Token costs for different representation levels of a document.
/// Used to inform users what budget is needed for higher-fidelity representations.
/// </summary>
public sealed record RepresentationCosts(
    int? FullTokens,       // Cost for full content (null if not available)
    int? StructureTokens,  // Cost for headline + structure (null if not available)
    int? HeadlineTokens    // Cost for headline only (null if not available)
);

/// <summary>
/// Interface for fetching document content for read operations.
/// </summary>
public interface IReadContentProvider
{
    /// <summary>
    /// Fetch a single document by URI.
    /// </summary>
    Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch multiple documents matching a glob pattern.
    /// </summary>
    Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken);

    /// <summary>
    /// Get ASCII tree of repository structure for a scope. Returns null if not supported.
    /// </summary>
    /// <param name="scope">Optional scope glob pattern (e.g., "file:///src/**"). Null for full repo.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetRepoTreeAsync(string? scope, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>
    /// Format a list of URIs as an ASCII tree. Returns null if not supported.
    /// </summary>
    /// <param name="uris">List of URIs to format.</param>
    /// <param name="foldersOnly">If true, shows only folders with file type counts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
