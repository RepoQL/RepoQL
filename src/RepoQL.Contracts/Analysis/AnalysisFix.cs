
namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Represents a set of replacements for a given document URI.
/// </summary>
public sealed class AnalysisFix
{
    public required string Uri { get; init; }
    public required IReadOnlyList<AnalysisReplacement> Replacements { get; init; }
    public string? Description { get; init; }
}
