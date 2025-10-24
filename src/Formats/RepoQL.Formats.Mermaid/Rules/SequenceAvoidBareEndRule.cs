using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Rules;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class SequenceAvoidBareEndRule : IRule
{
    public DiagnosticId Id => new("mmd/sequence/avoid-bare-end");
    public string Title => "Avoid bare 'end' in sequence texts";
    public string Description => "Wrap the word 'end' to avoid breaking the diagram.";
    public Severity DefaultSeverity => Severity.Warning;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "sequenceDiagram") yield break;
        foreach (var m in doc.Statements.OfType<SeqMessage>())
        {
            if (string.Equals(m.Text.Trim(), "end", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Diagnostic(Id, Severity.Warning, "Wrap 'end' (e.g., (end)).", m.TextSpan,
                    [new CodeFix("Wrap as (end)", [new TextChange(m.TextSpan, "(end)")])]);
            }
        }
    }
}