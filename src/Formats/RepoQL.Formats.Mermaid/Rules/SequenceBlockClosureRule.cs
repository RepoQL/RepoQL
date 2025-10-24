using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Rules;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class SequenceBlockClosureRule : IRule
{
    public DiagnosticId Id => new("mmd/sequence/block-end");
    public string Title => "Ensure sequence blocks have matching end";
    public string Description => "Detects unclosed alt/opt/loop blocks and suggests appending 'end'.";
    public Severity DefaultSeverity => Severity.Error;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "sequenceDiagram") yield break;
        var open = 0;
        foreach (var s in doc.Statements)
        {
            if (s is SeqBlockStart) open++;
            else if (s is SeqEnd && open > 0) open--;
        }
        if (open > 0)
        {
            var endPos = ctx.Tree.SourceText.Length;
            var fix = new CodeFix("Append end", [new TextChange(TextSpan.FromBounds(endPos, endPos), "\nend\n")]);
            yield return new Diagnostic(Id, Severity.Error, "Unclosed sequence block detected.", TextSpan.FromBounds(endPos, endPos),
                [fix]);
        }
    }
}