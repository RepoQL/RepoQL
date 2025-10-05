namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Describes a text replacement within a file.
/// </summary>
public sealed class AnalysisReplacement
{
    public required AnalysisRegion Region { get; init; }
    public required string NewText { get; init; }
}
