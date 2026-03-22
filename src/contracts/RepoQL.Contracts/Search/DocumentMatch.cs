namespace RepoQL.Contracts.Search;

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
    double Score,
    double SemanticScore = 0.0,
    double NameHitScore = 0.0,
    double RegexHitScore = 0.0,
    double ChunkOverlapScore = 0.0
);
