namespace RepoQL.Explore.Search;

/// <summary>
/// Parameters for an explore search operation.
/// </summary>
public record SearchParameters(
    string? Scope,
    string? Question,
    IReadOnlyList<string> Patterns,
    Intent Intent,
    int TokenBudget = 2000,
    IReadOnlyList<string>? PenalizePatterns = null
);

/// <summary>
/// Scope of a search result.
/// </summary>
public enum SearchScope
{
    /// <summary>A document (file) level result.</summary>
    Document,
    /// <summary>A symbol (function, class, etc.) within a document.</summary>
    Symbol
}

/// <summary>
/// A document match from the first search phase.
/// </summary>
public record DocumentMatch(
    string Uri,
    string? Headline,
    string? Structure,
    string? Snippet,
    string? Lang,
    string? SemanticType,
    double Score
);

/// <summary>
/// An object match from the second search phase.
/// </summary>
public record ObjectMatch(
    string Uri,
    string DocumentUri,
    string Kind,
    string? Symbol,
    string? Headline,
    string? Structure,
    string? Snippet,
    int LineStart,
    int LineEnd,
    string? Lang,
    string? SemanticType,
    double Score
)
{
    /// <summary>
    /// Mutable score for boosting operations.
    /// </summary>
    public double RawScore { get; set; } = Score;
}

/// <summary>
/// A search result ready for rendering.
/// </summary>
public record SearchResult(
    string Uri,
    SearchScope Scope,
    string? Kind,
    string? Symbol,
    string? Headline,
    string? Structure,
    string? Snippet,
    int? LineStart,
    int? LineEnd,
    string? Lang,
    string? SemanticType,
    double RawScore,
    int Confidence,
    IReadOnlyList<SearchResult>? ChildObjects = null
);

/// <summary>
/// Result from the search engine.
/// </summary>
public record SearchEngineResult(
    IReadOnlyList<SearchResult> Results,
    int TotalDocumentsMatched,
    int TotalObjectsMatched,
    TrustSignal? TrustSignal
);
