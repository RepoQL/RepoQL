namespace RepoQL.Grammar;

public interface IRule
{
    DiagnosticId Id { get; }
    string Title { get; }
    string Description { get; }
    Severity DefaultSeverity { get; }
    IEnumerable<Diagnostic> Analyze(RuleContext ctx);
}