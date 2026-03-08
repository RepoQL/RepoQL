namespace RepoQL.Explore.Search;

public interface IExploreCandidateService
{
    Task<ExploreCandidateResult> SearchAsync(
        string? query,
        string? scope,
        int k,
        CancellationToken cancellationToken);
}

public record ExploreCandidateResult(
    IReadOnlyList<ExploreCandidate> Candidates,
    int TotalMatched);

public record ExploreCandidate(
    Guid DocId,
    Guid NodeId,
    string Uri,
    string? Path,
    string NodeScope,
    string? Kind,
    string? Symbol,
    string? Lang,
    string? Mime,
    string? Headline,
    string? Structure,
    string? Snippet,
    int? LineStart,
    int? LineEnd,
    double BM25Score,
    double FuzzyScore,
    double SemScore,
    double Score,
    int Confidence,
    string? SemProvenance);
