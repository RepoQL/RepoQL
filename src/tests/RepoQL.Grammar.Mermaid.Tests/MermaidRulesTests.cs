#pragma warning disable IDE0005
using RepoQL.Formats.Mermaid;
using RepoQL.Formats.Mermaid.Rules;
using AwesomeAssertions;
using System.Text;

namespace RepoQL.Grammar.Mermaid.Tests;

public class MermaidRulesTests
{
    [Test]
    public Task Flowchart_LabelNeedsQuoting_Fixed()
    {
        var src = "flowchart LR\nC[Test|Pipe]\n";
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        diags.Any(d => d.Id == "mmd/flowchart/escape-labels").Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Flowchart_UnclosedShape_Fixed()
    {
        var src = "flowchart LR\nA[Unclosed\n";
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        diags.Any(d => d.Id == "mmd/flowchart/escape-labels").Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Pie_LabelAndValue_Fixed()
    {
        var src = "pie\nDogs : -10\n";
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        diags.Count(d => d.Id == "mmd/pie/labels-and-values").Should().BeGreaterThanOrEqualTo(1);
        return Task.CompletedTask;
    }

    [Test]
    public Task Sequence_BareEnd_Warned()
    {
        var src = "sequenceDiagram\nAlice->>Bob: end\n";
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        diags.Any(d => d.Id == "mmd/sequence/avoid-bare-end").Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Flowchart_Fixes_ApplyAndReparse()
    {
        var src = "flowchart LR\nC[Test|Pipe\n"; // unclosed + needs quoting
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        var edits = diags.SelectMany(d => d.Fixes).SelectMany(f => f.Edits).ToList();
        edits.Count.Should().BeGreaterThan(0);

        var fixedText = ApplyEdits(src, edits);
        var tree2 = lang.Parse(fixedText, new LanguageParseOptions { Tolerant = true });
        var doc2 = (MDocument)tree2.Root;
        var node = doc2.Statements.OfType<FlowNodeDecl>().FirstOrDefault(n => n.Id == "C");
        node.Should().NotBeNull();
        node!.LabelQuoted.Should().BeTrue();
        node.IsClosed.Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Pie_Fixes_ApplyAndReparse()
    {
        var src = "pie\nDogs : -10\n";
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        var edits = diags.SelectMany(d => d.Fixes).SelectMany(f => f.Edits).ToList();
        var fixedText = ApplyEdits(src, edits);
        var doc2 = (MDocument)lang.Parse(fixedText, new LanguageParseOptions { Tolerant = true }).Root;
        var e = doc2.Statements.OfType<PieEntry>().First();
        e.LabelQuoted.Should().BeTrue();
        e.Value.Should().BeGreaterThan(0);
        return Task.CompletedTask;
    }

    [Test]
    public Task Sequence_Fixes_ApplyAndReparse()
    {
        var src = "sequenceDiagram\nA->>B: end\n";
        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        var edits = diags.SelectMany(d => d.Fixes).SelectMany(f => f.Edits).ToList();
        var fixedText = ApplyEdits(src, edits);
        var doc2 = (MDocument)lang.Parse(fixedText, new LanguageParseOptions { Tolerant = true }).Root;
        var msg = doc2.Statements.OfType<SeqMessage>().First();
        msg.Text.Should().Be("(end)");
        return Task.CompletedTask;
    }

    private static string ApplyEdits(string s, IEnumerable<TextChange> edits)
    {
        var ordered = edits.OrderByDescending(e => e.Span.Start).ToList();
        var sb = new StringBuilder(s);
        foreach (var e in ordered)
        {
            sb.Remove(e.Span.Start, e.Span.Length);
            sb.Insert(e.Span.Start, e.NewText);
        }
        return sb.ToString();
    }

    [Test]
    public Task MoreComplexFlowchart_MultipleIssues_DetectedAndFixed()
    {
        // Flowchart with multiple issues: unclosed shape and label needing quoting, plus some edges
        var src = "flowchart LR\n" +
                  "A[Unclosed\n" +                // missing closing ']' (should be closed)
                  "B[Test|Pipe]\n" +              // label contains '|' (should be quoted)
                  "C(foo)-->D(bar)\n" +           // valid nodes/edges
                  "E{Guard?} -->|a -> b| F\n";    // edge with mid-label

        ILanguage lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var rules = new MermaidRuleSet();
        var ctx = new RuleContext { Language = lang, Tree = tree, FilePath = "mem://mmd", Cancel = default };
        var diags = new Linter(rules.Rules.ToArray()).Run(ctx).ToList();

        diags.Count(d => d.Id == "mmd/flowchart/escape-labels").Should().BeGreaterThanOrEqualTo(1);

        var edits = diags.SelectMany(d => d.Fixes).SelectMany(f => f.Edits).ToList();
        edits.Count.Should().BeGreaterThan(0);

        var fixedText = ApplyEdits(src, edits);
        var doc2 = (MDocument)lang.Parse(fixedText, new LanguageParseOptions { Tolerant = true }).Root;
        var nodes = doc2.Statements.OfType<FlowNodeDecl>().ToList();

        // A should now be closed
        var a = nodes.FirstOrDefault(n => n.Id == "A");
        a.Should().NotBeNull();
        a!.IsClosed.Should().BeTrue();

        // B should now have a quoted label
        var b = nodes.FirstOrDefault(n => n.Id == "B");
        b.Should().NotBeNull();
        b!.LabelQuoted.Should().BeTrue();

        return Task.CompletedTask;
    }
}
