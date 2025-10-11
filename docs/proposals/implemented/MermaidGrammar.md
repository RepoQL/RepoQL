Below is a **plug‑in Mermaid language** (Pidgin‑based) you can drop into the foundation we sketched (the `Lint.Core`, `Lint.Sarif`, `Lint.Parsing.Pidgin` abstractions). It includes:

* a **tolerant parser** (flowchart, sequenceDiagram, pie) that keeps spans for SARIF;
* a **syntax error surfacer**; and
* two **LLM‑error rules** with **autofixes**:

    * **Escape flowchart labels** (quote labels and escape inner quotes),
    * **Fix pie labels & values** (quote labels; require positive numbers),
    * **Avoid bare `end` in sequence texts** (wrap “end”),
    * **Unclosed shape** (insert the missing `]`, `)` or `}`).

> Docs rationale for the rules:
> • Flowcharts: quote “troublesome” characters and/or use entity codes. ([Mermaid Chart][1])
> • Pie labels must be in **double quotes** and values must be **positive**. ([Mermaid][2])
> • In sequence diagrams, **bare `end`** text can break parsing—enclose it. ([Mermaid Chart][3])

---

### `MermaidLanguage.cs`

```csharp
// MermaidLanguage.cs
// Requires NuGets: Pidgin, (your) Lint.Core, Lint.Parsing.Pidgin
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Lint.Core;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace Lint.Mermaid
{
    // ---------- AST ----------
    public abstract record MNode(string Kind, TextSpan Span) : ISyntaxNode
    {
        public virtual IEnumerable<ISyntaxNode> Children() => Array.Empty<ISyntaxNode>();
        string ISyntaxNode.Kind => Kind;
        TextSpan ISyntaxNode.Span => Span;
    }

    public sealed record MDocument(string DiagramKind, IReadOnlyList<MStmt> Statements, TextSpan Span) 
        : MNode("Document", Span);

    public abstract record MStmt(string SKind, TextSpan Span) : MNode(SKind, Span);

    // Flowchart
    public sealed record FlowNodeDecl(string Id, char ShapeOpen, char ShapeClose, string Label, bool LabelQuoted,
                                      bool IsClosed, TextSpan IdSpan, TextSpan LabelSpan, TextSpan Span)
        : MStmt("FlowNodeDecl", Span);

    public sealed record FlowEdge(string Src, string Arrow, string? MidLabel, string Dst,
                                  TextSpan SrcSpan, TextSpan ArrowSpan, TextSpan? MidLabelSpan, TextSpan DstSpan, TextSpan Span)
        : MStmt("FlowEdge", Span);

    // Sequence
    public sealed record SeqParticipant(string Keyword, string Name, string? Alias, TextSpan NameSpan, TextSpan Span)
        : MStmt("SeqParticipant", Span);

    public sealed record SeqMessage(string From, string Arrow, string To, string Text, TextSpan TextSpan, TextSpan Span)
        : MStmt("SeqMessage", Span);

    // Pie
    public sealed record PieEntry(string LabelRaw, bool LabelQuoted, double Value, TextSpan LabelSpan, TextSpan ValueSpan, TextSpan Span)
        : MStmt("PieEntry", Span);

    // Unknown / Skipped
    public sealed record UnknownStmt(string Raw, TextSpan Span) : MStmt("Unknown", Span);

    // ---------- Language ----------
    public sealed class MermaidLanguage /* : PidginLanguageBase if you have it */ : ILanguage
    {
        public string Name => "Mermaid";

        // Public entry
        public ISyntaxTree Parse(string text, LanguageParseOptions? options = null)
        {
            options ??= new();
            var (root, diags) = ParseDoc(text, options.Tolerant);
            return new Tree(text, root, diags);
        }

        public ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? _) => null;

        public string Print(ISyntaxNode node) => node switch
        {
            FlowNodeDecl n when n.LabelQuoted =>
                $"{n.Id}{n.ShapeOpen}\"{n.Label}\"{n.ShapeClose}",
            FlowNodeDecl n =>
                $"{n.Id}{n.ShapeOpen}{n.Label}{n.ShapeClose}",
            FlowEdge e when e.MidLabel is string ml =>
                $"{e.Src} {e.Arrow} |{ml}| {e.Dst}",
            FlowEdge e =>
                $"{e.Src} {e.Arrow} {e.Dst}",
            SeqParticipant p when p.Alias is string a =>
                $"{p.Keyword} {p.Name} as {a}",
            SeqParticipant p =>
                $"{p.Keyword} {p.Name}",
            SeqMessage m => $"{m.From}{m.Arrow}{m.To}: {m.Text}",
            PieEntry pe when pe.LabelQuoted => $"\"{pe.LabelRaw}\" : {pe.Value}",
            PieEntry pe => $"{pe.LabelRaw} : {pe.Value}",
            _ => node.ToString() ?? ""
        };

        // ---------- Syntax tree ----------
        private sealed class Tree : ISyntaxTree
        {
            public ISyntaxNode Root { get; }
            public string SourceText { get; }
            public IReadOnlyList<Diagnostic> ParseDiagnostics { get; }
            public Tree(string text, ISyntaxNode root, IReadOnlyList<Diagnostic> diags)
            { SourceText = text; Root = root; ParseDiagnostics = diags; }
            public ISyntaxTree WithChanges(params TextChange[] _changes) => throw new NotImplementedException();
        }

        // ---------- Parsers ----------
        private static readonly Parser<char, char> NL =
            Try(String("\r\n").Then(Return('\n'))).Or(Char('\n'));

        private static readonly Parser<char, Unit> OptWs =
            OneOf(Char(' '), Char('\t')).SkipMany();

        private static readonly Parser<char, string> Ident =
            Map((h, t) => h + new string(t.ToArray()),
                Letter.Or(Char('_')), LetterOrDigit.Or(Char('_')).Or(Char('-')).Many());

        private static Parser<char, (TextSpan Span, T Val)> WithSpan<T>(Parser<char, T> p) =>
            Map((s, v, e) => (TextSpan.FromBounds(s, e), v), GetOffset(), p, GetOffset());

        private static Parser<char, Unit> CommentLine =>
            Try(String("%%")).Then(AnyCharExcept('\n').Many()).Then(Return(Unit.Value));

        private static Parser<char, Unit> Directive =>
            Try(String("%%{")).Then(AnyCharExcept('%').Many()).Before(String("}%%")).Then(Return(Unit.Value));

        private static Parser<char, Unit> SkipJunk =>
            (from _ in (Directive.Or(CommentLine)).Then(NL.Optional()) select Unit.Value)
            .Or(Return(Unit.Value));

        private static Parser<char, string> Direction =
            OneOf(String("LR"), String("RL"), String("TB"), String("BT"), String("TD")).Select(x => x);

        private static Parser<char, string> DiagramKind =
            OneOf(
                String("flowchart").Select(_ => "flowchart"),
                String("graph").Select(_ => "flowchart"),
                String("sequenceDiagram"),
                String("pie")
            );

        private static Parser<char, (TextSpan Span, string Kind, string? Dir)> Header =>
            WithSpan(
                from _ in SkipJunk
                from kind in DiagramKind.Before(OptWs)
                from dir in Direction.Before(OptWs).Optional()
                select (kind, dir.HasValue ? dir.Value : null)
            ).Select(t => (t.Span, t.Val.kind, t.Val.Item2));

        // FLOW: NodeDecl  id[Label] / id("Label") / id{"Label"}
        private static Parser<char, FlowNodeDecl> FlowNodeDeclP =>
            WithSpan(
                from id in WithSpan(Ident.Before(OptWs))
                from open in OneOf(Char('['), Char('('), Char('{'))
                from labelStart in GetOffset()
                from (label, closed, quoted) in LabelUntil(open) // tolerant
                from closeSpan in GetOffset()
                select (id, open, labelStart, label, closed, quoted, closeSpan)
            ).Select(t =>
            {
                char close = t.Val.open switch { '[' => ']', '(' => ')', '{' => '}', _ => ']' };
                var lblSpan = TextSpan.FromBounds(t.Val.labelStart, t.Val.closed ? t.Val.closeSpan - 1 : t.Val.closeSpan);
                return new FlowNodeDecl(
                    Id: t.Val.id.Val, ShapeOpen: t.Val.open, ShapeClose: close,
                    Label: t.Val.label, LabelQuoted: t.Val.quoted, IsClosed: t.Val.closed,
                    IdSpan: t.Val.id.Span, LabelSpan: lblSpan, Span: t.Span
                );
            });

        private static Parser<char, (string label, bool closed, bool quoted)> LabelUntil(char open)
        {
            char close = open switch { '[' => ']', '(' => ')', '{' => '}', _ => ']' };
            var dq = Char('"');

            // Either: " ... " (quoted) or unquoted until close or EOL
            var quoted =
                from _ in dq
                from s in AnyCharExcept('"').ManyString()
                from c in dq
                select (s, true, true);

            var unquoted =
                from s in AnyCharExcept('\n', close).ManyString()
                from c in Char(close).Optional()
                select (s, c.HasValue, false);

            return Try(quoted).Or(unquoted);
        }

        // FLOW: Edge  A --> B   or   A --> |label| B
        private static readonly Parser<char, string> Arrow =
            OneOf(String("-->"), String("-.->"), String("==>"), String("---"), String("--x"), String("--o"), String("->"), String("->>"))
            .Select(x => x);

        private static Parser<char, FlowEdge> FlowEdgeP =>
            WithSpan(
                from src in WithSpan(Ident.Before(OptWs))
                from arr in WithSpan(Arrow.Before(OptWs))
                from mid in Try(Char('|').Then(AnyCharExcept('|').ManyString().Before(Char('|')).Before(OptWs)).Select(s => (s, true)))
                            .Or(Return(("", false)))
                from dst in WithSpan(Ident)
                select (src, arr, mid, dst)
            ).Select(t =>
                new FlowEdge(
                    t.Val.src.Val, t.Val.arr.Val, t.Val.mid.Item2 ? t.Val.mid.Item1 : null, t.Val.dst.Val,
                    t.Val.src.Span, t.Val.arr.Span, t.Val.mid.Item2 ? TextSpan.FromBounds(t.Span.Start + 0, t.Span.Start + 0) : null, // not used
                    t.Val.dst.Span, t.Span
                )
            );

        // SEQ: participant|actor NAME [as Alias]
        private static readonly Parser<char, string> SeqPartKw =
            OneOf(String("participant"), String("actor")).Select(x => x);

        private static Parser<char, SeqParticipant> SeqParticipantP =>
            WithSpan(
                from kw in SeqPartKw.Before(OneOf(Char(' '), Char('\t')).AtLeastOnce())
                from name in WithSpan(Ident)
                from alias in Try(OneOf(Char(' '), Char('\t')).AtLeastOnce().Then(String("as")).Then(OneOf(Char(' '), Char('\t')).AtLeastOnce()).Then(Ident)).Optional()
                select (kw, name, alias.HasValue ? alias.Value : null)
            ).Select(t => new SeqParticipant(t.Val.kw, t.Val.name.Val, t.Val.Item3, t.Val.name.Span, t.Span));

        // SEQ: message  A->>B: text...
        private static readonly Parser<char, string> SeqArrow =
            OneOf(String("->>"), String("-->>"), String("->"), String("-->"), String("-x"), String("--x"), String("-)"), String("--)" ))
            .Select(x => x);

        private static Parser<char, SeqMessage> SeqMessageP =>
            WithSpan(
                from fromId in Ident.Before(OptWs)
                from arr in SeqArrow.Before(OptWs)
                from toId in Ident.Before(OptWs)
                from _ in Char(':').Before(OptWs)
                from (tspan, text) in WithSpan(AnyCharExcept('\n').ManyString())
                select (fromId, arr, toId, text, tspan)
            ).Select(t => new SeqMessage(t.Val.fromId, t.Val.arr, t.Val.toId, t.Val.text, t.Val.tspan, t.Span));

        // PIE:  "label" : 12.3
        private static readonly Parser<char, string> Quoted =
            Char('"').Then(AnyCharExcept('"').ManyString(), (q, s) => s).Before(Char('"'));

        private static Parser<char, PieEntry> PieEntryP =>
            WithSpan(
                from lbl in Try(Quoted.Select(s => (s, true))).Or(Ident.Select(s => (s, false)))
                from _ in OptWs.Before(Char(':')).Before(OptWs)
                from numTxt in Digit.AtLeastOnceString()
                                .Then(Try(Char('.').Then(Digit.AtLeastOnceString())).Or(Return("")), (i, f) => i + f)
                let val = double.TryParse(numTxt, out var d) ? d : double.NaN
                select (lbl, val)
            ).Select(t => new PieEntry(t.Val.lbl.Item1, t.Val.lbl.Item2, t.Val.val,
                                       // crude spans for label/value: recompute from text when needed in rules
                                       LabelSpan: t.Span, ValueSpan: t.Span, Span: t.Span));

        // STATEMENT per-kind
        private static Parser<char, MStmt> FlowStmt =>
            Try(FlowNodeDeclP).Select<MStmt>(x => x)
            .Or(Try(FlowEdgeP).Select<MStmt>(x => x))
            .Or(WithSpan(AnyCharExcept('\n').ManyString()).Select(t => new UnknownStmt(t.Val, t.Span)));

        private static Parser<char, MStmt> SeqStmt =>
            Try(SeqParticipantP).Select<MStmt>(x => x)
            .Or(Try(SeqMessageP).Select<MStmt>(x => x))
            .Or(WithSpan(AnyCharExcept('\n').ManyString()).Select(t => new UnknownStmt(t.Val, t.Span)));

        private static Parser<char, MStmt> PieStmt =>
            Try(PieEntryP).Select<MStmt>(x => x)
            .Or(WithSpan(AnyCharExcept('\n').ManyString()).Select(t => new UnknownStmt(t.Val, t.Span)));

        private static Parser<char, IReadOnlyList<MStmt>> BodyFor(string kind) =>
            (kind switch
            {
                "flowchart" => FlowStmt.Before(NL.Optional()).Many(),
                "sequenceDiagram" => SeqStmt.Before(NL.Optional()).Many(),
                "pie" => PieStmt.Before(NL.Optional()).Many(),
                _ => WithSpan(AnyCharExcept('\n').ManyString()).Select(t => new UnknownStmt(t.Val, t.Span)).Before(NL.Optional()).Many()
            }).Select(l => (IReadOnlyList<MStmt>)l.ToList());

        private static (MDocument Root, List<Diagnostic> Diags) ParseDoc(string text, bool tolerant)
        {
            var diags = new List<Diagnostic>();

            try
            {
                var hdr = Header.Before(NL.Optional()).ParseOrThrow(text);
                var body = BodyFor(hdr.Kind).ParseOrThrow(text[(hdr.Span.End)..]);
                // Rebase body spans to absolute coords
                var stmts = body.Select(s => s with { Span = Rebase(s.Span, hdr.Span.End) }).ToList();
                // Fix up label spans where we used coarse spans:
                // (for rules we will re-locate by regex in context)
                var root = new MDocument(hdr.Kind, stmts, TextSpan.FromBounds(0, text.Length));
                return (root, diags);
            }
            catch (ParseException pe) when (tolerant)
            {
                diags.Add(new Diagnostic(
                    new("mmd/syntax-error"), Severity.Error, pe.Message,
                    new TextSpan(Math.Max(0, pe.ErrorPos), 1),
                    Array.Empty<CodeFix>()
                ));
                var root = new MDocument("unknown", Array.Empty<MStmt>(), TextSpan.FromBounds(0, text.Length));
                return (root, diags);
            }

            static TextSpan Rebase(TextSpan s, int delta) => TextSpan.FromBounds(s.Start + delta, s.End + delta);
        }
    }
}
```

---

### `MermaidRules.cs`

```csharp
// MermaidRules.cs
// Requires: Lint.Core, Lint.Sarif (for emission elsewhere)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Lint.Core;
using Lint.Mermaid;

namespace Lint.Mermaid.Rules
{
    // 1) Flowchart: escape labels & fix unclosed shapes
    public sealed class FlowchartEscapeLabelsRule : IRule
    {
        public DiagnosticId Id => new("mmd/flowchart/escape-labels");
        public string Title => "Quote/escape troublesome label characters";
        public string Description => "Flowchart labels with characters like ], ), }, |, :, or quotes should be quoted and inner quotes escaped.";
        public Severity DefaultSeverity => Severity.Warning;

        private static readonly Regex NeedsQuoting = new(@"[|\]\)\}\:<>\,]", RegexOptions.CultureInvariant);
        private static readonly Regex InnerQuote = new("\"");

        public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
        {
            if (ctx.Language is not MermaidLanguage) yield break;
            if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "flowchart") yield break;

            foreach (var n in doc.Statements.OfType<FlowNodeDecl>())
            {
                // Unclosed shape → insert missing closer
                if (!n.IsClosed)
                {
                    var closer = n.ShapeClose.ToString();
                    var fix = new CodeFix($"Insert '{closer}'", new[]
                    {
                        new TextChange(TextSpan.FromBounds(n.Span.End, n.Span.End), closer)
                    });
                    yield return new Diagnostic(new("mmd/flowchart/unclosed-shape"), Severity.Error,
                        $"Unclosed node label. Expected '{closer}'.",
                        n.Span, new[] { fix });
                }

                // If not quoted and contains troublesome chars → quote
                if (!n.LabelQuoted && NeedsQuoting.IsMatch(n.Label))
                {
                    var quoted = $"\"{n.Label.Replace("\"", "&quot;")}\"";
                    var fix = new CodeFix("Wrap label in quotes (escape inner quotes)", new[]
                    {
                        new TextChange(n.LabelSpan, quoted)
                    });
                    yield return new Diagnostic(Id, Severity.Warning,
                        "Quote the label or escape special characters.",
                        n.LabelSpan, new[] { fix });
                }

                // If quoted but contains bare double quotes → escape to &quot;
                if (n.LabelQuoted && InnerQuote.IsMatch(n.Label))
                {
                    var escaped = n.Label.Replace("\"", "&quot;");
                    var fix = new CodeFix("Escape inner quotes (&quot;)", new[]
                    {
                        new TextChange(n.LabelSpan, $"\"{escaped}\"")
                    });
                    yield return new Diagnostic(Id, Severity.Warning,
                        "Escape inner double quotes inside quoted label.",
                        n.LabelSpan, new[] { fix });
                }
            }
        }
    }

    // 2) Pie: labels in quotes; values positive
    public sealed class PieSafetyRule : IRule
    {
        public DiagnosticId Id => new("mmd/pie/labels-and-values");
        public string Title => "Quote pie labels and use positive values";
        public string Description => "Pie entries should be \"label\" : number (> 0).";
        public Severity DefaultSeverity => Severity.Error;

        private static readonly Regex PieLine = new(@"^(?<lbl>.+?)\s*:\s*(?<num>[-+]?\d+(\.\d+)?)\s*$", RegexOptions.Multiline);

        public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
        {
            if (ctx.Language is not MermaidLanguage) yield break;
            if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "pie") yield break;

            var text = ctx.Tree.SourceText;
            foreach (var m in PieLine.Matches(text))
            {
                var full = new TextSpan(m.Index, m.Length);
                var lbl = m.Groups["lbl"].Value.Trim();
                var lblSpan = new TextSpan(m.Groups["lbl"].Index, m.Groups["lbl"].Length);
                var numSpan = new TextSpan(m.Groups["num"].Index, m.Groups["num"].Length);

                // Quote label if not "..."
                if (!(lbl.StartsWith("\"") && lbl.EndsWith("\"")))
                {
                    var qlbl = $"\"{lbl.Replace("\"", "&quot;")}\"";
                    yield return new Diagnostic(Id, Severity.Error, "Pie label must be quoted.",
                        lblSpan, new[] { new CodeFix("Quote label", new[] { new TextChange(lblSpan, qlbl) }) });
                }

                // Positive value only
                if (!double.TryParse(m.Groups["num"].Value, out var v) || !(v > 0))
                {
                    var safe = Math.Abs(v);
                    yield return new Diagnostic(Id, Severity.Error, "Pie value must be a positive number.",
                        numSpan, new[] { new CodeFix("Make value positive", new[] { new TextChange(numSpan, safe.ToString()) }) });
                }
            }
        }
    }

    // 3) Sequence: avoid bare 'end' text (wrap)
    public sealed class SequenceAvoidBareEndRule : IRule
    {
        public DiagnosticId Id => new("mmd/sequence/avoid-bare-end");
        public string Title => "Avoid bare 'end' in sequence texts";
        public string Description => "Wrap the word 'end' to avoid breaking the diagram.";
        public Severity DefaultSeverity => Severity.Warning;

        public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
        {
            if (ctx.Language is not MermaidLanguage) yield break;
            if (ctx.Tree.Root is not MDocument doc || doc.DiagramKind != "sequenceDiagram") yield break;

            foreach (var msg in doc.Statements.OfType<SeqMessage>())
            {
                if (string.Equals(msg.Text.Trim(), "end", StringComparison.OrdinalIgnoreCase))
                {
                    var fix = new CodeFix("Wrap as (end)", new[]
                    {
                        new TextChange(msg.TextSpan, "(end)")
                    });
                    yield return new Diagnostic(Id, Severity.Warning,
                        "Wrap the word 'end' (e.g., (end) or \"end\").",
                        msg.TextSpan, new[] { fix });
                }
            }
        }
    }

    // 4) Surface parser diagnostics as rule results
    public sealed class SyntaxErrorsRule : IRule
    {
        public DiagnosticId Id => new("mmd/syntax-error");
        public string Title => "Syntax error";
        public string Description => "Parser-reported Mermaid syntax issues.";
        public Severity DefaultSeverity => Severity.Error;

        public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
            => ctx.Tree.ParseDiagnostics;
    }

    // Pack
    public sealed class MermaidRuleSet : IRuleSet
    {
        public IReadOnlyList<IRule> Rules { get; } = new IRule[]
        {
            new SyntaxErrorsRule(),
            new FlowchartEscapeLabelsRule(),
            new PieSafetyRule(),
            new SequenceAvoidBareEndRule()
        };
    }
}
```

---

### Example usage

```csharp
// Program.cs (example)
using System;
using Lint.Core;
using Lint.Mermaid;
using Lint.Mermaid.Rules;
using Lint.Sarif;

class Program
{
    static void Main()
    {
        var src = """
        %%{init: {"flowchart": {"htmlLabels": true}} }%%
        flowchart LR
          A[Unclosed                      %% error
          B[He said "Hello"]              %% escape inner quote
          C[Test|Pipe]                    %% needs quoting
          A --> |a -> b| B
        ---
        pie title: Pets
          Dogs : -10
          Cats : 30
          Fish : 20
        ---
        sequenceDiagram
          Alice->>Bob: end
        """;

        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });

        var rules = new MermaidRuleSet();
        var ctx = new RuleContext
        {
            Language = lang,
            Tree = tree,
            FilePath = "file:///diagram.mmd",
            Cancel = default
        };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        var sarif = SarifEmitter.ToSarif(ctx.FilePath, tree.SourceText, diags, toolName: "MermaidNetLint");
        Console.WriteLine(sarif.ToJson());
    }
}
```

**Notes**

* Flowchart label escaping follows the docs’ “quote troublesome characters” / “entity codes” guidance; the fix uses quotes plus `&quot;` for inner quotes. ([Mermaid Chart][1])
* Pie labels are enforced as `"label" : value`, and values > 0 per spec. ([Mermaid][2])
* Sequence bare `end` is wrapped to avoid parse issues. ([Mermaid Chart][3])

This skeleton is intentionally **tolerant** and easy to extend “by example”: add a new statement parser (e.g., `subgraph ... end`), plug it into the per‑kind `...Stmt` choice, and write a small rule that leverages the precise `TextSpan` for fixes.

[1]: https://docs.mermaidchart.com/mermaid-oss/syntax/flowchart.html "
      Mermaid Chart - Create complex, visual diagrams with text. A smarter way
      of creating diagrams.
    "
[2]: https://mermaid.js.org/syntax/pie.html "Pie chart diagrams | Mermaid"
[3]: https://docs.mermaidchart.com/mermaid-oss/syntax/sequenceDiagram.html?utm_source=chatgpt.com "Sequence diagrams | Mermaid"
