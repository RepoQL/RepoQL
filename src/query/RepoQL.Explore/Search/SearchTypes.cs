using RepoQL.Contracts.Search;

namespace RepoQL.Explore.Search;

/// <summary>
/// Parameters for an explore search operation.
/// </summary>
public record SearchParameters(
    string? Scope,
    string? Keywords,
    IReadOnlyList<string> Patterns,
    int Breadth = 5,
    int TokenBudget = 2000,
    IReadOnlyList<string>? PenalizePatterns = null,
    string? Question = null
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
    IReadOnlyList<SearchResult>? ChildObjects = null,
    string? Provenance = null
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
