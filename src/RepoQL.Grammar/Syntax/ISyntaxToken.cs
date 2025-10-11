using RepoQL.Grammar.Core;

namespace RepoQL.Grammar.Syntax;

public interface ISyntaxToken
{
    string Kind { get; }
    string Text { get; }
    TextSpan Span { get; }
    IReadOnlyList<ISyntaxTrivia> Leading { get; }
    IReadOnlyList<ISyntaxTrivia> Trailing { get; }
}