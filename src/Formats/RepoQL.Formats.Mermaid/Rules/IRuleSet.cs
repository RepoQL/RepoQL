namespace RepoQL.Formats.Mermaid.Rules;

public interface IRuleSet
{
    IReadOnlyList<IRule> Rules { get; }
}
