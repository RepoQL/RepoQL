namespace RepoQL.Xray.Search;

/// <summary>
/// Engine for executing xray searches.
/// </summary>
public interface IXraySearchEngine
{
    /// <summary>
    /// Execute a search and return grouped, scored results.
    /// </summary>
    Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default implementation of the xray search engine.
/// </summary>
public sealed class XraySearchEngine : IXraySearchEngine
{
    private readonly IDocumentSearchService _documentSearch;
    private readonly IObjectSearchService _objectSearch;

    public XraySearchEngine(
        IDocumentSearchService documentSearch,
        IObjectSearchService objectSearch)
    {
        _documentSearch = documentSearch ?? throw new ArgumentNullException(nameof(documentSearch));
        _objectSearch = objectSearch ?? throw new ArgumentNullException(nameof(objectSearch));
    }

    public async Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        CancellationToken cancellationToken)
    {
        // 1. Plan query strategy
        var plan = QueryStrategy.Plan(parameters);

        // 2. Execute document search
        var docResult = await _documentSearch.SearchAsync(
            parameters.Scope,
            parameters.Question,
            plan.DocumentLimit * 2, // Fetch extra to account for filtering
            cancellationToken).ConfigureAwait(false);

        var documents = docResult.Documents.Take(plan.DocumentLimit).ToList();

        // 3. Execute object search (if planned)
        IReadOnlyList<ObjectMatch> objects;
        if (plan.FetchObjects && documents.Count > 0)
        {
            var docUris = documents.Select(d => d.Uri).ToList();
            objects = await _objectSearch.SearchInDocumentsAsync(
                docUris,
                parameters.Question,
                plan.ObjectsPerDocument,
                cancellationToken).ConfigureAwait(false);

            // 4. Apply chunk proximity boosts to objects
            ChunkProximityBooster.ApplyBoosts((IList<ObjectMatch>)objects, docResult.ChunkScores);
        }
        else
        {
            objects = [];
        }

        // 5. Group by file (3 snippets + rest as headlines)
        var groups = FileGrouper.Group(documents, objects);

        // 6. Flatten to results
        var results = FileGrouper.Flatten(groups).ToList();

        // 7. Apply pattern boosts and penalties
        var boostPatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.Patterns));
        PatternBooster.ApplyBoosts(results, boostPatterns);

        if (parameters.PenalizePatterns is { Count: > 0 })
        {
            var penalizePatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.PenalizePatterns));
            PatternBooster.ApplyPenalties(results, penalizePatterns);
        }

        // 8. Normalize confidence
        ConfidenceNormalizer.NormalizeInPlace(results);

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: docResult.Documents.Count,
            TotalObjectsMatched: objects.Count,
            IndexerStatus: null // Populated by caller (XrayTool) with timing info
        );
    }
}
