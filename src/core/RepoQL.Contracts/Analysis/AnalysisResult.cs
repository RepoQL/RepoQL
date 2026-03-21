using System.Text.Json.Nodes;

namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Result emitted by an analyzer for a specific document or graph element.
/// </summary>
public sealed class AnalysisResult
{
    public required string SemanticKey { get; init; }
    public required string RuleId { get; init; }
    public required string Source { get; init; }
    public required string Kind { get; init; }
    public AnalysisSeverity Severity { get; init; } = AnalysisSeverity.Warning;
    public required string Message { get; init; }
    public AnalysisTarget? Target { get; init; }
    public JsonObject? Data { get; init; }
    public IReadOnlyList<AnalysisFix>? Fixes { get; init; }
    public bool AutoFixable { get; init; }
}
