using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class FlowEnd(TextSpan span) : MStmt("mmd_flow_end", span)
{ }
