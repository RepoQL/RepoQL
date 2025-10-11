using RepoQL.Grammar.Diagnostics;

namespace RepoQL.Grammar.Rules;

public sealed class Linter(params IRule[] rules)
{
    private readonly IReadOnlyList<IRule> _rules = rules;

    public IEnumerable<Diagnostic> Run(RuleContext ctx)
        => _rules.SelectMany(r => r.Analyze(ctx));
}