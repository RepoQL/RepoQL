using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Purpose: Renders directory trees for read modifier requests with progressive verbosity.
/// Complexity: Delegates formatting to IReadContentProvider and enforces budget-based fallbacks.
/// </summary>
public sealed class TreeHandler : IModifierHandler
{
    private readonly IReadContentProvider _contentProvider;

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
        _ = parameter;
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
                exceedsBudget: false);
        }

        var namesTree = await _contentProvider
            .FormatAsTreeAsync(uris, foldersOnly: false, includeHeadlines: false, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(namesTree))
            throw new InvalidOperationException("Tree formatter returned empty output.");

        var namesTokens = TokenEstimator.EstimateTokens(namesTree);
        if (namesTokens <= tokenBudget)
        {
            return BuildResult(namesTree, namesTokens, documents.Count, filesConsulted,
                new Dictionary<string, object> { ["verbosity"] = "names" },
                exceedsBudget: false);
        }

        var foldersTree = await _contentProvider
            .FormatAsTreeAsync(uris, foldersOnly: true, includeHeadlines: false, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(foldersTree))
            throw new InvalidOperationException("Tree formatter returned empty output.");

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        var exceedsBudget = foldersTokens > tokenBudget;

        return BuildResult(foldersTree, foldersTokens, documents.Count, filesConsulted,
            new Dictionary<string, object> { ["verbosity"] = "folders" },
            exceedsBudget: exceedsBudget);
    }

    private static ModifierResult BuildResult(
        string content,
        int tokenCount,
        int totalAvailable,
        IReadOnlyList<string> filesConsulted,
        IReadOnlyDictionary<string, object> extra,
        bool exceedsBudget)
    {
        return new ModifierResult(
            content,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: totalAvailable,
            ExceedsBudget: exceedsBudget,
            Metadata: new ResultMetadata(filesConsulted, Warning: null, Extra: extra));
    }
}
