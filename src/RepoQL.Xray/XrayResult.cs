namespace RepoQL.Xray;

/// <summary>
/// A single search result to be rendered.
/// Contains all the data needed for any representation level.
/// </summary>
/// <param name="Uri">The file or symbol URI, possibly with line fragment.</param>
/// <param name="Confidence">Confidence score 1-100.</param>
/// <param name="Kind">null for documents, "class"/"method"/etc for objects.</param>
/// <param name="Headline">One-line summary of the result.</param>
/// <param name="Structure">Outline/hierarchy for Standard representation.</param>
/// <param name="Snippet">Code excerpt for Rich representation.</param>
/// <param name="Lang">Language hint for snippet code fence (e.g., "csharp").</param>
/// <param name="SemanticType">Semantic type for grouping (e.g., "markdown.doc", "code.csharp").</param>
/// <param name="ChildObjects">Nested child objects beneath this result (e.g., methods within a file).</param>
public record XrayResult(
    string Uri,
    int Confidence,
    string? Kind,
    string? Headline,
    string? Structure,
    string? Snippet,
    string? Lang,
    string? SemanticType = null,
    IReadOnlyList<XrayResult>? ChildObjects = null
);
