using System.Collections.Immutable;
using RepoQL.Grammar.Core;
using RepoQL.Grammar.Syntax;

namespace RepoQL.Grammar.Tests;

internal sealed class Node(string kind, TextSpan span, IReadOnlyList<ISyntaxNode>? children = null, string? text = null)
    : ISyntaxNode
{
    public string Kind { get; } = kind;
    public TextSpan Span { get; } = span;
    public string? Text { get; } = text;
    private IReadOnlyList<ISyntaxNode> ChildrenList { get; } = children ?? ImmutableArray<ISyntaxNode>.Empty;
    public IEnumerable<ISyntaxNode> Children() => ChildrenList;
    public override string ToString() => Text is null ? Kind : $"{Kind}({Text})";
}