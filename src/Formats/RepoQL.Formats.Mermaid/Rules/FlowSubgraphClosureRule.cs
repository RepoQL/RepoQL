using RepoQL.Formats.Mermaid.Core;
using RepoQL.Formats.Mermaid.Diagnostics;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class FlowSubgraphClosureRule : IRule
{
    public DiagnosticId Id => new("mmd/flow/subgraph-end");
    public string Title => "Ensure subgraph has matching end";
    public string Description => "Detects unclosed subgraph blocks and suggests appending 'end'.";
    public Severity DefaultSeverity => Severity.Error;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "flowchart") yield break;
        var open = 0;
        foreach (var s in doc.Statements)
        {
            if (s is FlowSubgraphStart) open++;
            else if (s is FlowEnd && open > 0) open--;
        }
        if (open > 0)
        {
            var endPos = ctx.Tree.SourceText.Length;
            var fix = new CodeFix("Append end", [new TextChange(TextSpan.FromBounds(endPos, endPos), "\nend\n")]);
            yield return new Diagnostic(Id, Severity.Error, "Unclosed subgraph detected.", TextSpan.FromBounds(endPos, endPos),
                [fix]);
        }
    }
}
