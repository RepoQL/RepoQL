using RepoQL.Grammar.Rules;

namespace RepoQL.Grammar.Tests;

internal sealed class RuleSet(params IRule[] rules) : IRuleSet
{
    public IReadOnlyList<IRule> Rules { get; } = rules;
}