using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class MarkdownXrayRichTests
{
    [Test]
    public async Task Markdown_Xray_Reports_Images_Tables_Frontmatter()
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

        var md = "---\nlayout: post\ntitle: Sample\ntags: [auth, oauth]\n---\n\n# Title\n\n![img](path.png)\n\n| a | b |\n|---|---|\n| 1 | 2 |\n";
        var uri = repo.AddOrUpdateText("docs/rich.md", md);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("#auth");
        hl.Should().Contain("#oauth");
    }
}
