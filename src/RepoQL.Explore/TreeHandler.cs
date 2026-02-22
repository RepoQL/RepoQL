using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Purpose: Renders directory trees with progressive verbosity under a token budget.
/// Complexity: Delegates formatting to IReadContentProvider and enforces budget-based fallbacks
/// (headlines → files → folders). Shared core algorithm via FitToBudgetAsync; modifier-specific
/// wrapping in ExecuteAsync.
/// </summary>
public sealed class TreeHandler : IModifierHandler
{
    private readonly IReadContentProvider _contentProvider;

    public enum TreeDetailLevel { Folders, Files, Headlines }

    /// <summary>
    /// Result of fitting a tree to a token budget. Contains the rendered tree,
    /// its token count, and which verbosity level was used.
    /// </summary>
    public record TreeFitResult(string Content, int TokenCount, TreeDetailLevel Verbosity);

    public TreeHandler(IReadContentProvider contentProvider)
    {
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
    }

    public string ModifierName => "tree";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tries tree representations from <paramref name="maxLevel"/> down to Folders,
    /// returning the richest one that fits within <paramref name="tokenBudget"/>.
    /// Returns null if even the folders tree exceeds the budget.
    /// </summary>
    public static async Task<TreeFitResult?> FitToBudgetAsync(
        IReadContentProvider provider,
        IReadOnlyList<string> uris,
        int tokenBudget,
        TreeDetailLevel maxLevel,
        CancellationToken ct)
    {
        if (uris.Count == 0)
            return null;

        // Try headlines if allowed
        if (maxLevel >= TreeDetailLevel.Headlines)
        {
            var headlineTree = await provider
                .FormatAsTreeAsync(uris, foldersOnly: false, includeHeadlines: true, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(headlineTree))
            {
                var headlineTokens = TokenEstimator.EstimateTokens(headlineTree);
                if (headlineTokens <= tokenBudget)
                    return new TreeFitResult(headlineTree, headlineTokens, TreeDetailLevel.Headlines);
            }
        }

        // Try files if allowed
        if (maxLevel >= TreeDetailLevel.Files)
        {
            var filesTree = await provider
                .FormatAsTreeAsync(uris, foldersOnly: false, includeHeadlines: false, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(filesTree))
            {
                var filesTokens = TokenEstimator.EstimateTokens(filesTree);
                if (filesTokens <= tokenBudget)
                    return new TreeFitResult(filesTree, filesTokens, TreeDetailLevel.Files);
            }
        }

        // Always try folders as final fallback
        var foldersTree = await provider
            .FormatAsTreeAsync(uris, foldersOnly: true, includeHeadlines: false, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(foldersTree))
            return null;

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        if (foldersTokens <= tokenBudget)
            return new TreeFitResult(foldersTree, foldersTokens, TreeDetailLevel.Folders);

        return null;
    }

    public async Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        var requestedLevel = ParseDetailLevel(parameter);
        var filesConsulted = documents.Select(doc => doc.Uri).ToList();

        if (documents.Count == 0)
        {
            const string emptyContent = "No files matched.";
            var emptyTokens = TokenEstimator.EstimateTokens(emptyContent);
            return new ModifierResult(
                emptyContent,
                TokenCount: emptyTokens,
                TotalAvailable: 0,
                Shown: 0,
                ExceedsBudget: emptyTokens > tokenBudget,
                Metadata: new ResultMetadata(filesConsulted, Warning: null, Extra: new Dictionary<string, object>()));
        }

        var uris = documents.Select(d => d.Uri).ToList();
        var fit = await FitToBudgetAsync(_contentProvider, uris, tokenBudget, requestedLevel, ct)
            .ConfigureAwait(false);

        if (fit is not null)
        {
            var warning = fit.Verbosity < requestedLevel
                ? FormatDowngradeWarning(requestedLevel, fit.Verbosity)
                : null;

            return BuildResult(fit.Content, fit.TokenCount, documents.Count, filesConsulted,
                new Dictionary<string, object> { ["verbosity"] = fit.Verbosity.ToString().ToLowerInvariant() },
                exceedsBudget: false, warning: warning);
        }

        // Even folders exceeds budget — return folders anyway, marked as exceeding
        var foldersTree = await _contentProvider
            .FormatAsTreeAsync(uris, foldersOnly: true, includeHeadlines: false, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(foldersTree))
            throw new InvalidOperationException("Tree formatter returned empty output.");

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        var foldersWarning = FormatDowngradeWarning(requestedLevel, TreeDetailLevel.Folders);

        return BuildResult(foldersTree, foldersTokens, documents.Count, filesConsulted,
            new Dictionary<string, object> { ["verbosity"] = "folders" },
            exceedsBudget: true, warning: foldersWarning);
    }

    private static string? FormatDowngradeWarning(TreeDetailLevel requested, TreeDetailLevel actual)
    {
        if (actual >= requested)
            return null;

        return requested switch
        {
            TreeDetailLevel.Headlines => actual == TreeDetailLevel.Files
                ? "Showing files only - request headlines with higher budget for file headlines"
                : "Showing folders only - request headlines with higher budget for file headlines",
            TreeDetailLevel.Files => "Showing folders only - request files with higher budget for file names",
            _ => null
        };
    }

    private static TreeDetailLevel ParseDetailLevel(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return TreeDetailLevel.Files; // default

        return parameter.Trim().ToLowerInvariant() switch
        {
            "folders" => TreeDetailLevel.Folders,
            "files" => TreeDetailLevel.Files,
            "headlines" => TreeDetailLevel.Headlines,
            _ => throw new ArgumentException(
                $"tree modifier parameter must be 'folders', 'files', or 'headlines', got '{parameter}'.")
        };
    }

    private static ModifierResult BuildResult(
        string content,
        int tokenCount,
        int totalAvailable,
        IReadOnlyList<string> filesConsulted,
        IReadOnlyDictionary<string, object> extra,
        bool exceedsBudget,
        string? warning)
    {
        return new ModifierResult(
            content,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: totalAvailable,
            ExceedsBudget: exceedsBudget,
            Metadata: new ResultMetadata(filesConsulted, Warning: warning, Extra: extra));
    }
}
