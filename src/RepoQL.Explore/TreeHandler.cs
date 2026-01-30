using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Purpose: Renders directory trees for read modifier requests with progressive verbosity.
/// Complexity: Delegates formatting to IReadContentProvider and enforces budget-based fallbacks.
/// Supports detail levels: folders, files (default), headlines.
/// </summary>
public sealed class TreeHandler : IModifierHandler
{
    private readonly IReadContentProvider _contentProvider;

    private enum TreeDetailLevel { Folders, Files, Headlines }

    public TreeHandler(IReadContentProvider contentProvider)
    {
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
    }

    public string ModifierName => "tree";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

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

        // Try headlines if requested
        if (requestedLevel == TreeDetailLevel.Headlines)
        {
            var headlineTree = await _contentProvider
                .FormatAsTreeAsync(uris, foldersOnly: false, includeHeadlines: true, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(headlineTree))
                throw new InvalidOperationException("Tree formatter returned empty output.");

            var headlineTokens = TokenEstimator.EstimateTokens(headlineTree);
            if (headlineTokens <= tokenBudget)
            {
                return BuildResult(headlineTree, headlineTokens, documents.Count, filesConsulted,
                    new Dictionary<string, object> { ["verbosity"] = "headlines" },
                    exceedsBudget: false, warning: null);
            }
            // Fall through to files level with warning
        }

        // Try files if requested or falling back from headlines
        if (requestedLevel >= TreeDetailLevel.Files)
        {
            var namesTree = await _contentProvider
                .FormatAsTreeAsync(uris, foldersOnly: false, includeHeadlines: false, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(namesTree))
                throw new InvalidOperationException("Tree formatter returned empty output.");

            var namesTokens = TokenEstimator.EstimateTokens(namesTree);
            if (namesTokens <= tokenBudget)
            {
                var warning = requestedLevel == TreeDetailLevel.Headlines
                    ? "Showing files only - request headlines with higher budget for file summaries"
                    : null;
                return BuildResult(namesTree, namesTokens, documents.Count, filesConsulted,
                    new Dictionary<string, object> { ["verbosity"] = "files" },
                    exceedsBudget: false, warning: warning);
            }
            // Fall through to folders level with warning
        }

        // Always try folders as final fallback
        var foldersTree = await _contentProvider
            .FormatAsTreeAsync(uris, foldersOnly: true, includeHeadlines: false, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(foldersTree))
            throw new InvalidOperationException("Tree formatter returned empty output.");

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        var exceedsBudget = foldersTokens > tokenBudget;

        var foldersWarning = requestedLevel switch
        {
            TreeDetailLevel.Headlines => "Showing folders only - request headlines with higher budget for file summaries",
            TreeDetailLevel.Files => "Showing folders only - request files with higher budget for file names",
            _ => null
        };

        return BuildResult(foldersTree, foldersTokens, documents.Count, filesConsulted,
            new Dictionary<string, object> { ["verbosity"] = "folders" },
            exceedsBudget: exceedsBudget, warning: foldersWarning);
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
