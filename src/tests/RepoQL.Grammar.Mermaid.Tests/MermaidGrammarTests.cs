#pragma warning disable IDE0005
using RepoQL.Formats.Mermaid;
using AwesomeAssertions;
using RepoQL.Grammar.Language;

namespace RepoQL.Grammar.Mermaid.Tests;

public class MermaidGrammarTests
{
    [Test]
    public Task Flowchart_NodesAndEdge_Parsed()
    {
        var src = "flowchart LR\nA[Hello]\nA --> B\n";
        var lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var doc = (MDocument)tree.Root;

        doc.DiagramKind.Should().Be("flowchart");
        doc.Statements.Count.Should().BeGreaterThanOrEqualTo(2);
        doc.Statements.OfType<FlowNodeDecl>().Any(n => n.Id == "A" && n.Label == "Hello").Should().BeTrue();
        doc.Statements.OfType<FlowEdge>().Any(e => e.Src == "A" && e.Dst == "B").Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Sequence_Message_Parsed()
    {
        var src = "sequenceDiagram\nparticipant Alice\nAlice->>Bob: hello\n";
        var lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var doc = (MDocument)tree.Root;

        doc.DiagramKind.Should().Be("sequenceDiagram");
        doc.Statements.OfType<SeqParticipant>().Any(p => p.Name == "Alice").Should().BeTrue();
        doc.Statements.OfType<SeqMessage>().Any(m => m.From == "Alice" && m.To == "Bob" && m.Text.Contains("hello")).Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Pie_Entries_Parsed()
    {
        var src = "pie\n\"Dogs\" : 10\n\"Cats\" : 20\n";
        var lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var doc = (MDocument)tree.Root;

        doc.DiagramKind.Should().Be("pie");
        var entries = doc.Statements.OfType<PieEntry>().ToList();
        entries.Count.Should().Be(2);
        entries.Select(e => e.LabelRaw).Should().Contain("Dogs");
        entries.Select(e => e.Value).Should().Contain(10);
        return Task.CompletedTask;
    }

    [Test]
    public Task Flow_Subgraph_ClassDef_Click_Parsed()
    {
        var src = "flowchart LR\nsubgraph Group A\nA[Node]\nend\nclassDef red fill:#f00,stroke:#000\nclick A href \"https://example.com\" \"Go\"\n";
        var lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var doc = (MDocument)tree.Root;

        doc.DiagramKind.Should().Be("flowchart");
        doc.Statements.OfType<FlowSubgraphStart>().Any().Should().BeTrue();
        doc.Statements.OfType<FlowEnd>().Any().Should().BeTrue();
        doc.Statements.OfType<ClassDef>().Any(c => c.Name == "red").Should().BeTrue();
        doc.Statements.OfType<ClickStmt>().Any(c => c.NodeId == "A").Should().BeTrue();
        return Task.CompletedTask;
    }

    [Test]
    public Task Sequence_Blocks_Parsed()
    {
        var src = "sequenceDiagram\nparticipant A\nalt condition\nA->>A: work\nend\nopt maybe\nA->>A: try\nend\n";
        var lang = new MermaidLanguage();
        var tree = lang.Parse(src, new LanguageParseOptions { Tolerant = true });
        var doc = (MDocument)tree.Root;

        doc.Statements.OfType<SeqBlockStart>().Count().Should().Be(2);
        doc.Statements.OfType<SeqEnd>().Count().Should().Be(2);
        return Task.CompletedTask;
    }
}
