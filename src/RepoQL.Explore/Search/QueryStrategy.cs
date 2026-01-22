namespace RepoQL.Explore.Search;

/// <summary>
/// How to fetch objects.
/// </summary>
public enum ObjectFetchMode
{
    /// <summary>Don't fetch objects.</summary>
    None,
    /// <summary>Fetch objects from top N documents only.</summary>
    TopDocumentsOnly
}

/// <summary>
/// Query execution plan.
/// </summary>
public record QueryPlan(
    bool FetchDocuments,
    bool FetchObjects,
    int DocumentLimit,
    int ObjectsPerDocument,
    ObjectFetchMode ObjectMode
);

/// <summary>
/// Determines query strategy based on intent and parameters.
/// Fetch limits are fixed per intent - display limits are handled separately by rendering.
/// </summary>
public static class QueryStrategy
{
    // Fixed fetch limits - we always fetch enough to have good coverage
    private const int ExploreFetchLimit = 50;
    private const int FindFetchLimit = 20;
    private const int ReadFetchLimit = 15;

    /// <summary>
    /// Plan the query execution based on search parameters.
    /// </summary>
    public static QueryPlan Plan(SearchParameters parameters)
    {
        var hasQuestion = !string.IsNullOrWhiteSpace(parameters.Question);

        return parameters.Intent switch
        {
            // Explore: Fast overview, documents only (unless question provided)
            Intent.Inventory when !hasQuestion => new QueryPlan(
                FetchDocuments: true,
                FetchObjects: false,
                DocumentLimit: ExploreFetchLimit,
                ObjectsPerDocument: 0,
                ObjectMode: ObjectFetchMode.None),

            Intent.Inventory when hasQuestion => new QueryPlan(
                FetchDocuments: true,
                FetchObjects: true,
                DocumentLimit: 10,
                ObjectsPerDocument: 3,
                ObjectMode: ObjectFetchMode.TopDocumentsOnly),

            // Find: Documents + objects from top docs
            Intent.Locate => new QueryPlan(
                FetchDocuments: true,
                FetchObjects: hasQuestion,
                DocumentLimit: FindFetchLimit,
                ObjectsPerDocument: hasQuestion ? 5 : 0,
                ObjectMode: hasQuestion ? ObjectFetchMode.TopDocumentsOnly : ObjectFetchMode.None),

            // Read: Focus on objects when question provided
            Intent.Inspect => new QueryPlan(
                FetchDocuments: true,
                FetchObjects: hasQuestion,
                DocumentLimit: ReadFetchLimit,
                ObjectsPerDocument: hasQuestion ? 8 : 0,
                ObjectMode: hasQuestion ? ObjectFetchMode.TopDocumentsOnly : ObjectFetchMode.None),

            _ => new QueryPlan(
                FetchDocuments: true,
                FetchObjects: false,
                DocumentLimit: ExploreFetchLimit,
                ObjectsPerDocument: 0,
                ObjectMode: ObjectFetchMode.None)
        };
    }
}
