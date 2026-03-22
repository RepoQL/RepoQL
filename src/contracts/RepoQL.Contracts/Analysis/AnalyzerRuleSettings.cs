namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Configuration for a single analyzer rule (sourced from .editorconfig or defaults).
/// </summary>
public sealed record AnalyzerRuleSettings
{
    public string RuleId { get; init; } = string.Empty;
    public AnalysisSeverity Severity { get; init; } = AnalysisSeverity.Warning;
    public bool EnableAutoFix { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}
