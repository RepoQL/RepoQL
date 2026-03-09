using RepoQL.Formats.Mermaid.Core;
using RepoQL.Formats.Mermaid.Syntax;

namespace RepoQL.Formats.Mermaid;

public abstract class MNode(string kind, TextSpan span) : ISyntaxNode
{
    public string Kind { get; } = kind;
    public TextSpan Span { get; } = span;
    public virtual IEnumerable<ISyntaxNode> Children() { yield break; }
}

// Flowchart extras

// Sequence blocks
