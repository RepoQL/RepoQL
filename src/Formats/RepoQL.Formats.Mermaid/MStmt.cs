using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public abstract class MStmt(string sKind, TextSpan span) : MNode(sKind, span);
