using RepoQL.Grammar.Core;

namespace RepoQL.Formats.Mermaid;

public abstract class MStmt(string sKind, TextSpan span) : MNode(sKind, span);