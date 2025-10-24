using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Rules;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class PieSafetyRule : IRule
{
    public DiagnosticId Id => new("mmd/pie/labels-and-values");
    public string Title => "Quote pie labels and use positive values";
    public string Description => "Pie entries should be \"label\" : number (> 0).";
    public Severity DefaultSeverity => Severity.Error;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "pie") yield break;
        foreach (var e in doc.Statements.OfType<PieEntry>())
        {
            if (!e.LabelQuoted)
            {
                var qlbl = "\"" + e.LabelRaw.Replace("\"", "&quot;") + "\"";
                yield return new Diagnostic(Id, Severity.Error, "Pie label must be quoted.", e.LabelSpan,
                    [new CodeFix("Quote label", [new TextChange(e.LabelSpan, qlbl)])]);
            }
            if (!(e.Value > 0))
            {
                var abs = Math.Abs(e.Value);
                var repl = abs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                yield return new Diagnostic(Id, Severity.Error, "Pie value must be positive.", e.ValueSpan,
                    [new CodeFix("Make value positive", [new TextChange(e.ValueSpan, repl)])]);
            }
        }
    }
}