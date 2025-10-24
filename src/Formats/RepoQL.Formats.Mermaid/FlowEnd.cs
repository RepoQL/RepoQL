using RepoQL.Grammar.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class FlowEnd(TextSpan span) : MStmt("mmd_flow_end", span)
{ }