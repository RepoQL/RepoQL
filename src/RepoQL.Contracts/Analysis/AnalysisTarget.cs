namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Identifies the graph element associated with an analysis result.
/// </summary>
public sealed class AnalysisTarget
{
    public Guid? NodeId { get; init; }
    public Guid? EdgeId { get; init; }
    public Guid? SpanId { get; init; }
    public RepoUri? TargetUri { get; init; }
}
