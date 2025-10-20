using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Tests.Scaffolding;

namespace RepoQL.Tests;

internal class MermaidXrayTests
{
    [Test]
    public async Task Mermaid_Indexer_Populates_Xray_Fields_On_Artifact()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.Xray";
            options.AddFormat(new FormatDescriptor(
                SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"),
                new Formats.Mermaid.MermaidLoader(),
                new Formats.Mermaid.MermaidAnalyzer(),
                new Formats.Mermaid.MermaidLoader(),
                ["mermaid", "mmd"]));
        });

        var mmd = "flowchart TD\nA[Start] --> B{Check}\nB -->|Yes| C[OK]\nB -->|No| D[Fail]\n";
        var uri = repo.AddOrUpdateText("diagrams/flow.mmd", mmd);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("diagram:");
        hl.Should().Contain("nodes");
        hl.Should().Contain("edges");

        artifact.Summary!.ToLowerInvariant().Should().Contain("diagram:");
        artifact.Summary!.ToLowerInvariant().Should().Contain("flow:");

        artifact.Structure!.Should().Contain("Flowchart");
        artifact.Structure!.Should().Contain("- A: Start");
    }
}
