using RepoQL.Grammar.Diagnostics;

namespace RepoQL.Grammar.Rules;

public interface IRule
{
    DiagnosticId Id { get; }
    string Title { get; }
    string Description { get; }
    Severity DefaultSeverity { get; }
    IEnumerable<Diagnostic> Analyze(RuleContext ctx);
}