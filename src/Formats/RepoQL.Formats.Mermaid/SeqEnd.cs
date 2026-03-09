using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class SeqEnd(TextSpan span) : MStmt("mmd_seq_end", span)
{ }
