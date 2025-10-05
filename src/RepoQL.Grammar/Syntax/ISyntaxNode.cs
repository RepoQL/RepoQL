namespace RepoQL.Grammar;

public interface ISyntaxNode
{
    string Kind { get; }
    TextSpan Span { get; }
    IEnumerable<ISyntaxNode> Children();
}