namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Represents a span within a document. Either line-based or character-based coordinates
///     may be supplied. When both are provided, line/column should take precedence.
/// </summary>
public sealed class AnalysisRegion
{
    public int? StartLine { get; init; }
    public int? StartColumn { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public int? StartChar { get; init; }
    public int? EndChar { get; init; }

    public bool IsLineBased => StartLine.HasValue && EndLine.HasValue;
}
