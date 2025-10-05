namespace RepoQL.Grammar;

public sealed class Linter(params IRule[] rules)
{
    private readonly IReadOnlyList<IRule> _rules = rules;

    public IEnumerable<Diagnostic> Run(RuleContext ctx)
        => _rules.SelectMany(r => r.Analyze(ctx));
}