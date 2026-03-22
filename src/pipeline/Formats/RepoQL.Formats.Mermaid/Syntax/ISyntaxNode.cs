using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid.Syntax;

public interface ISyntaxNode
{
    string Kind { get; }

    TextSpan Span { get; }

    IEnumerable<ISyntaxNode> Children();
}
