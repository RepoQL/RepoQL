using RepoQL.Grammar.Core;

namespace RepoQL.Grammar.Syntax;

public interface ISyntaxNode
{
    string Kind { get; }
    TextSpan Span { get; }
    IEnumerable<ISyntaxNode> Children();
}