using RepoQL.Formats.Mermaid.Core;
using RepoQL.Formats.Mermaid.Diagnostics;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class FlowchartEscapeLabelsRule : IRule
{
    private static readonly System.Buffers.SearchValues<char> ProblemChars = System.Buffers.SearchValues.Create("|])}:<>\"");

    public DiagnosticId Id => new("mmd/flowchart/escape-labels");
    public string Title => "Quote/escape troublesome label characters";
    public string Description => "Flowchart labels with characters like ], ), }, |, :, or quotes should be quoted and inner quotes escaped.";
    public Severity DefaultSeverity => Severity.Warning;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "flowchart") yield break;
        foreach (var n in doc.Statements.OfType<FlowNodeDecl>())
        {
            var fixes = new List<TextChange>();
            if (!n.IsClosed)
            {
                fixes.Add(new TextChange(new TextSpan(n.Span.End, 0), n.ShapeClose.ToString()));
            }
            if (!n.LabelQuoted && NeedsQuote(n.Label))
            {
                fixes.Add(new TextChange(n.LabelSpan, "\"" + n.Label.Replace("\"", "&quot;") + "\""));
            }
            if (fixes.Count > 0)
            {
                yield return new Diagnostic(Id, Severity.Warning, "Quote/escape label or close shape.", n.Span,
                    [new CodeFix("Fix node", fixes)]);
            }
        }
    }

    private static bool NeedsQuote(string s) => s.AsSpan().IndexOfAny(ProblemChars) >= 0;
}
