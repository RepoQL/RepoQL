
namespace RepoQL.Formats.Mermaid.Rules;

public sealed class MermaidRuleSet : IRuleSet
{
    public IReadOnlyList<IRule> Rules { get; } =
    [
        new FlowchartEscapeLabelsRule(),
        new PieSafetyRule(),
        new SequenceAvoidBareEndRule(),
        new FlowSubgraphClosureRule(),
        new SequenceBlockClosureRule()
    ];
}
