using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Rules;

namespace RepoQL.Grammar.Tests;

internal sealed class DuplicateVarRule : IRule
{
    public DiagnosticId Id => new("mini/duplicate-var");
    public string Title => "Duplicate variable";
    public string Description => "Variable declared more than once";
    public Severity DefaultSeverity => Severity.Error;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        var text = ctx.Tree.SourceText;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decl in ctx.Tree.Root.Children().Where(n => n.Kind == "LetDecl"))
        {
            var id = decl.Children().FirstOrDefault(n => n.Kind == "Identifier");
            if (id is null) continue;
            var name = (id as Node)?.Text ?? (id.Span.Length > 0 ? text.Substring(id.Span.Start, id.Span.Length) : string.Empty);
            if (!seen.Add(name))
            {
                yield return new Diagnostic(Id, Severity.Error, $"Duplicate '{name}'", id.Span, []);
            }
        }
    }
}