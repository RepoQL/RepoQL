using RepoQL.Grammar.Syntax;

namespace RepoQL.Grammar.Rules;

public static class Q
{
    public static IEnumerable<ISyntaxNode> OfKind(this ISyntaxNode root, params string[] kinds)
        => Flatten(root).Where(n => kinds.Contains(n.Kind));

    private static IEnumerable<ISyntaxNode> Flatten(ISyntaxNode n)
    {
        yield return n;
        foreach (var c in n.Children())
        {
            foreach (var d in Flatten(c))
            {
                yield return d;
            }
        }
    }
}
