using RepoQL.Formats.Mermaid.Core;
using RepoQL.Formats.Mermaid.Syntax;

namespace RepoQL.Formats.Mermaid;

public sealed class MDocument(string diagramKind, IReadOnlyList<MStmt> statements, TextSpan span)
    : MNode("mmd_document", span)
{
    public string DiagramKind { get; } = diagramKind;
    public IReadOnlyList<MStmt> Statements { get; } = statements;
    public override IEnumerable<ISyntaxNode> Children() => Statements;
}
