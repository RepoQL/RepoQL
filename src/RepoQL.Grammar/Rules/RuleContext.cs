using RepoQL.Grammar.Language;
using RepoQL.Grammar.Syntax;

namespace RepoQL.Grammar.Rules;

public sealed class RuleContext
{
    public required ILanguage Language { get; init; }
    public required ISyntaxTree Tree { get; init; }
    public ISemanticModel? SemanticModel { get; init; }
    public required string FilePath { get; init; }
    public required CancellationToken Cancel { get; init; }

    public IEnumerable<T> DescendantsOfKind<T>(string kind) where T : class, ISyntaxNode
        => TreeDescendants(Tree.Root).Where(n => n.Kind == kind).Cast<T>();

    private static IEnumerable<ISyntaxNode> TreeDescendants(ISyntaxNode n)
    {
        var stack = new Stack<ISyntaxNode>(); stack.Push(n);
        while (stack.Count > 0)
        {
            var x = stack.Pop(); yield return x;
            foreach (var c in x.Children()) stack.Push(c);
        }
    }
}