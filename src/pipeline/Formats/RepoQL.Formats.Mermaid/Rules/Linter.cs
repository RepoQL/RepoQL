using RepoQL.Formats.Mermaid.Diagnostics;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class Linter(params IRule[] rules)
{
    private readonly IReadOnlyList<IRule> _rules = rules;

    public IEnumerable<Diagnostic> Run(RuleContext ctx)
        => _rules.SelectMany(rule => rule.Analyze(ctx));
}
