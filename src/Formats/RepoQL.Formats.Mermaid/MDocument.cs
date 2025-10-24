using RepoQL.Grammar.Core;
using RepoQL.Grammar.Syntax;

namespace RepoQL.Formats.Mermaid;

public sealed class MDocument(string diagramKind, IReadOnlyList<MStmt> statements, TextSpan span)
    : MNode("mmd_document", span)
{
    public string DiagramKind { get; } = diagramKind;
    public IReadOnlyList<MStmt> Statements { get; } = statements;
    public override IEnumerable<ISyntaxNode> Children() => Statements;
}