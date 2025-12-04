namespace RepoQL.Rendering.Search;

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
/// - If a file has object matches, show objects only (not the file itself)
/// - Limit to 3 snippets per file, show rest as headlines
/// </summary>
public static class FileGrouper
{
    /// <summary>
    /// Maximum number of objects to show with snippets per file.
    /// </summary>
    public const int MaxSnippetsPerFile = 3;

    /// <summary>
    /// Group documents and objects by file.
    /// </summary>
    public static IReadOnlyList<FileGroup> Group(
        IReadOnlyList<DocumentMatch> documents,
        IReadOnlyList<ObjectMatch> objects)
    {
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
                // File has objects → show objects, NOT the file
                groups.Add(new FileGroup(
                    doc.Uri,
                    doc.Score,
                    Document: null,  // Don't show document when we have objects
                    SnippetObjects: docObjects.Take(MaxSnippetsPerFile).ToList(),
                    HeadlineObjects: docObjects.Skip(MaxSnippetsPerFile).ToList()
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
                SnippetObjects: docObjects.Take(MaxSnippetsPerFile).ToList(),
                HeadlineObjects: docObjects.Skip(MaxSnippetsPerFile).ToList()
            ));
        }

        // Sort groups by file score descending
        return groups.OrderByDescending(g => g.FileScore).ToList();
    }

    /// <summary>
    /// Flatten file groups into a list of search results.
    /// </summary>
    public static IReadOnlyList<SearchResult> Flatten(IReadOnlyList<FileGroup> groups)
    {
        var results = new List<SearchResult>();

        foreach (var group in groups)
        {
            if (group.Document is not null)
            {
                // Show document (no objects in this file)
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
                    Confidence: 0  // Will be normalized later
                ));
            }

            // Show snippet objects (top 3)
            foreach (var obj in group.SnippetObjects)
            {
                results.Add(new SearchResult(
                    Uri: obj.Uri,
                    Scope: SearchScope.Symbol,
                    Kind: obj.Kind,
                    Symbol: obj.Symbol,
                    Headline: obj.Headline,
                    Structure: null,
                    Snippet: obj.Snippet,
                    LineStart: obj.LineStart,
                    LineEnd: obj.LineEnd,
                    Lang: obj.Lang,
                    SemanticType: obj.SemanticType,
                    RawScore: obj.RawScore,
                    Confidence: 0
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
                    Structure: null,
                    Snippet: null,  // No snippet for headline-only
                    LineStart: obj.LineStart,
                    LineEnd: obj.LineEnd,
                    Lang: obj.Lang,
                    SemanticType: obj.SemanticType,
                    RawScore: obj.RawScore,
                    Confidence: 0
                ));
            }
        }

        return results;
    }
}
