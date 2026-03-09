using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class FlowSubgraphStart(string name, TextSpan span) : MStmt("mmd_flow_subgraph_start", span)
{
    public string Name { get; } = name;
}
