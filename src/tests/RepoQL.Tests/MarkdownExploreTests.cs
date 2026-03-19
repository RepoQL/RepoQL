using RepoQL.Data.DuckDB;
using System;
using System.Linq;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem.InMemory;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class MarkdownExploreTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Markdown_Indexer_Populates_Explore_Fields_On_Artifact()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.Explore";
            options.AddMarkdownFormat();
        });

        var md = "# Title\n\n## Section A\nText.\n\n```csharp\n// code\n```\n\n[link](#section-a)\n";
        var uri = repo.AddOrUpdateText("docs/readme.md", md);

        await repo.IndexAsync();

        var doc = await repo.WaitForDocumentAsync(uri, DefaultTimeout) ?? throw new TimeoutException("Document was not indexed");
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!;
        hl.Should().StartWith("Title |");
        artifact.Summary!.Should().Contain("Topics:");
        artifact.Structure!.Should().Contain("- Title");
        artifact.Structure!.Should().Contain("- Section A");
        artifact.Structure!.Should().NotContain("[... ");
    }

    [Test]
    public async Task MarkdownHeadingNodes_IncludeHeadlineAndStructure()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/notes.md");
        fs.AddOrUpdateText("notes.md", "# Title\n\n## Features\nSome text.\n");

        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var headingNode = records.Nodes
            .Single(n => string.Equals(n.Kind, "md_heading", StringComparison.OrdinalIgnoreCase)
                         && n.Props?["text"]?.GetValue<string>() == "Features");

        headingNode.Headline.Should().Be("H2 · Features");
        // Structure is empty for a heading with no child headings or code blocks
        headingNode.Structure.Should().BeEmpty();
    }
}
