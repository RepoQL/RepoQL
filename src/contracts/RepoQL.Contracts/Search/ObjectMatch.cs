namespace RepoQL.Contracts.Search;

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
    double Score,
    double SemanticScore = 0.0,
    double NameHitScore = 0.0,
    double RegexHitScore = 0.0,
    double ChunkOverlapScore = 0.0
)
{
    /// <summary>
    /// Mutable score for boosting operations.
    /// </summary>
    public double RawScore { get; set; } = Score;
}
