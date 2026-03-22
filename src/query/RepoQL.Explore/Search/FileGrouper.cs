using RepoQL.Contracts.Search;

namespace RepoQL.Explore.Search;

/// <summary>
/// A group of results for a single file.
/// </summary>
public record FileGroup(
    string DocumentUri,
    double FileScore,
    DocumentMatch? Document,
    IReadOnlyList<ObjectMatch> SnippetObjects,
    IReadOnlyList<ObjectMatch> HeadlineObjects
);

/// <summary>
/// Groups search results by file.
/// Key rules:
/// - If a file has object matches, show the document WITH nested child objects
/// - Show top snippets per file up to the configured limit, show rest as headlines
/// </summary>
public static class FileGrouper
{
    /// <summary>
    /// Minimum snippets to show per file when calculating dynamic limits.
    /// </summary>
    public const int MinSnippetsPerFile = 2;

    private const int InspectIntentMaxSnippetsPerFile = 15;

    private const int AverageSnippetCost = 150;

    /// <summary>
    /// Group documents and objects by file.
    /// </summary>
    public static IReadOnlyList<FileGroup> Group(
        IReadOnlyList<DocumentMatch> documents,
        IReadOnlyList<ObjectMatch> objects,
        int maxSnippetsPerFile = 3)
    {
        var snippetLimit = Math.Max(0, maxSnippetsPerFile);

        // Group objects by their parent document
        var objectsByDoc = objects
            .GroupBy(o => o.DocumentUri)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.RawScore).ToList());

        var groups = new List<FileGroup>();
        var processedDocs = new HashSet<string>();

        // Process documents in score order
        foreach (var doc in documents.OrderByDescending(d => d.Score))
        {
            processedDocs.Add(doc.Uri);

            if (objectsByDoc.TryGetValue(doc.Uri, out var docObjects) && docObjects.Count > 0)
            {
                // File has objects → show document WITH nested child objects
                groups.Add(new FileGroup(
                    doc.Uri,
                    doc.Score,
                    Document: doc,  // Show document with child objects
                    SnippetObjects: docObjects.Take(snippetLimit).ToList(),
                    HeadlineObjects: docObjects.Skip(snippetLimit).ToList()
                ));
            }
            else
            {
                // File has no objects → show as document
                groups.Add(new FileGroup(
                    doc.Uri,
                    doc.Score,
                    Document: doc,
                    SnippetObjects: [],
                    HeadlineObjects: []
                ));
            }
        }

        // Handle any objects whose documents weren't in the document list
        foreach (var (docUri, docObjects) in objectsByDoc)
        {
            if (processedDocs.Contains(docUri))
                continue;

            var maxScore = docObjects.Max(o => o.RawScore);
            groups.Add(new FileGroup(
                docUri,
                maxScore,
                Document: null,
                SnippetObjects: docObjects.Take(snippetLimit).ToList(),
                HeadlineObjects: docObjects.Skip(snippetLimit).ToList()
            ));
        }

        // Sort groups by file score descending
        return groups.OrderByDescending(g => g.FileScore).ToList();
    }

    /// <summary>
    /// Calculate dynamic snippet limit from breadth and token budget.
    /// Low breadth = more snippets per file (depth). High breadth = fewer snippets (coverage).
    /// </summary>
    public static int CalculateSnippetLimit(int breadth, int tokenBudget, int resultCount)
    {
        var clampedBreadth = Math.Clamp(breadth, 1, 10);
        var maxForBreadth = clampedBreadth switch
        {
            <= 2 => InspectIntentMaxSnippetsPerFile,
            <= 4 => 8,
            <= 6 => 5,
            <= 8 => 3,
            _ => 2
        };

        var boundedResultCount = Math.Max(1, Math.Min(resultCount, 10));
        var perFileBudget = Math.Max(0, tokenBudget) / boundedResultCount;
        var snippetsFromBudget = perFileBudget / AverageSnippetCost;
        var dynamicLimit = Math.Max(MinSnippetsPerFile, snippetsFromBudget);

        return Math.Min(maxForBreadth, dynamicLimit);
    }

    /// <summary>
    /// Flatten file groups into a list of search results with nested child objects.
    /// </summary>
    public static IReadOnlyList<SearchResult> Flatten(IReadOnlyList<FileGroup> groups)
    {
        var results = new List<SearchResult>();

        foreach (var group in groups)
        {
            if (group.Document is not null)
            {
                // Convert snippet objects to SearchResult children
                var childObjects = new List<SearchResult>();

                // Add snippet objects as children
                foreach (var obj in group.SnippetObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: obj.Snippet,
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.RawScore,
                        Confidence: 0,
                        ChildObjects: null,
                        Provenance: null
                    ));
                }

                // Add headline objects (rest - no snippet) as children
                foreach (var obj in group.HeadlineObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: null,  // No snippet for headline-only
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.RawScore,
                        Confidence: 0,
                        ChildObjects: null,
                        Provenance: null
                    ));
                }

                // Show document with child objects (if any)
                results.Add(new SearchResult(
                    Uri: group.Document.Uri,
                    Scope: SearchScope.Document,
                    Kind: null,
                    Symbol: null,
                    Headline: group.Document.Headline,
                    Structure: group.Document.Structure,
                    Snippet: group.Document.Snippet,
                    LineStart: null,
                    LineEnd: null,
                    Lang: group.Document.Lang,
                    SemanticType: group.Document.SemanticType,
                    RawScore: group.Document.Score,
                    Confidence: 0,  // Will be normalized later
                    ChildObjects: childObjects.Count > 0 ? childObjects : null,
                    Provenance: null
                ));
            }
            else
            {
                // Document not in results, but objects are - show objects without parent
                // (This handles orphaned objects whose documents weren't matched)

                // Show snippet objects
                foreach (var obj in group.SnippetObjects)
                {
                    results.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: obj.Snippet,
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.RawScore,
                        Confidence: 0,
                        ChildObjects: null,
                        Provenance: null
                    ));
                }

                // Show headline objects (rest - no snippet)
                foreach (var obj in group.HeadlineObjects)
                {
                    results.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: null,  // No snippet for headline-only
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.RawScore,
                        Confidence: 0,
                        ChildObjects: null,
                        Provenance: null
                    ));
                }
            }
        }

        return results;
    }
}
