namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Severity levels emitted by analyzers.
/// </summary>
public enum AnalysisSeverity
{
    None = 0,
    Suggestion = 1,
    Warning = 2,
    Error = 3
}
