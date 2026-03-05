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
/// Determines query strategy based on breadth and parameters.
/// Fetch limits scale with breadth — high breadth fetches more documents, low breadth fetches more objects.
/// </summary>
public static class QueryStrategy
{
    /// <summary>
    /// Plan the query execution based on search parameters.
    /// </summary>
    public static QueryPlan Plan(SearchParameters parameters)
    {
        var hasQuestion = !string.IsNullOrWhiteSpace(parameters.Question);
        var breadth = Math.Clamp(parameters.Breadth, 1, 10);

        // Scale document fetch limit with breadth: low breadth = fewer docs, high breadth = more
        var documentLimit = breadth switch
        {
            <= 2 => 15,
            <= 4 => 20,
            <= 6 => 25,
            <= 8 => 35,
            _ => 50
        };

        // Scale objects per document inversely with breadth
        var objectsPerDocument = breadth switch
        {
            <= 2 => 8,
            <= 4 => 6,
            <= 6 => 5,
            <= 8 => 3,
            _ => 0
        };

        // High breadth without a question: documents only (inventory scan)
        if (breadth >= 8 && !hasQuestion)
        {
            return new QueryPlan(
                FetchDocuments: true,
                FetchObjects: false,
                DocumentLimit: documentLimit,
                ObjectsPerDocument: 0,
                ObjectMode: ObjectFetchMode.None);
        }

        return new QueryPlan(
            FetchDocuments: true,
            FetchObjects: hasQuestion && objectsPerDocument > 0,
            DocumentLimit: documentLimit,
            ObjectsPerDocument: hasQuestion ? objectsPerDocument : 0,
            ObjectMode: hasQuestion && objectsPerDocument > 0 ? ObjectFetchMode.TopDocumentsOnly : ObjectFetchMode.None);
    }
}
