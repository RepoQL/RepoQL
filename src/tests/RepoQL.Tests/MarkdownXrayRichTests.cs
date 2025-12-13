using RepoQL.Data.DuckDB;
using System;
using AwesomeAssertions;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class MarkdownXrayRichTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Markdown_Xray_Reports_Images_Tables_Frontmatter()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.Xray";
            options.AddMarkdownFormat();
        });

        var md = "---\nlayout: post\ntitle: Sample\ntags: [auth, oauth]\n---\n\n# Title\n\n![img](path.png)\n\n| a | b |\n|---|---|\n| 1 | 2 |\n";
        var uri = repo.AddOrUpdateText("docs/rich.md", md);

        await repo.IndexAsync();

        var doc = await repo.WaitForDocumentAsync(uri, DefaultTimeout) ?? throw new TimeoutException("Document was not indexed");
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("#auth");
        hl.Should().Contain("#oauth");
        artifact.Summary!.Should().Contain("layout: post");
    }
}
