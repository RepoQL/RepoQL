namespace RepoQL.Grammar;

public interface ISyntaxToken
{
    string Kind { get; }
    string Text { get; }
    TextSpan Span { get; }
    IReadOnlyList<ISyntaxTrivia> Leading { get; }
    IReadOnlyList<ISyntaxTrivia> Trailing { get; }
}