using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Rules;

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

public sealed class MermaidRuleSet : IRuleSet
{
    public IReadOnlyList<IRule> Rules { get; } =
    [
        new FlowchartEscapeLabelsRule(),
        new PieSafetyRule(),
        new SequenceAvoidBareEndRule(),
        new FlowSubgraphClosureRule(),
        new SequenceBlockClosureRule()
    ];
}

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
