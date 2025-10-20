using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Tests.Scaffolding;

namespace RepoQL.Tests;

internal class MarkdownXrayTests
{
    [Test]
    public async Task Markdown_Indexer_Populates_Xray_Fields_On_Artifact()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.Xray";
            options.AddFormat(new FormatDescriptor(
                SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"),
                new Formats.Markdown.MarkdownLoader(),
                new Formats.Markdown.MarkdownAnalyzer(),
                new Formats.Markdown.MarkdownLoader(),
                ["md", "markdown"]));
        });

        var md = "# Title\n\n## Section A\nText.\n\n```csharp\n// code\n```\n\n[link](#section-a)\n";
        var uri = repo.AddOrUpdateText("docs/readme.md", md);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("markdown");
        hl.Should().Contain("lines");
        (hl.Contains("topics:") || hl.Contains("lang:")).Should().BeTrue();
        artifact.Summary!.ToLowerInvariant().Should().Contain("sections");
        artifact.Structure!.Should().Contain("- Title");
        artifact.Structure!.Should().Contain("- Section A");
    }
}
