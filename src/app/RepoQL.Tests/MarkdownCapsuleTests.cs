using System;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.InMemory;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class MarkdownCapsuleTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Markdown_Parses_Capsule_With_All_Sections()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/docs/capsules.md");

        var md = """
            # Document

            ## Capsule: CircuitBreaker

            **Invariant**
            Stop calls to an unhealthy dependency until recovery is shown.

            **Example**
            Service marks the dependency unhealthy after repeated failures.
            //BOUNDARY: Do not retry through the breaker.

            **Depth**
            - Distinction: a breaker protects availability; backoff reduces pressure.
            - SeeAlso: `RetryPolicy`, `Backpressure`.
            """;

        fs.AddOrUpdateText("docs/capsules.md", md);
        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var capsuleNode = records.Nodes
            .SingleOrDefault(n => n.Kind == "md_capsule");

        capsuleNode.Should().NotBeNull();
        capsuleNode!.Props!["name"]!.GetValue<string>().Should().Be("CircuitBreaker");
        capsuleNode.Props["invariant"]!.GetValue<string>().Should().Contain("Stop calls to an unhealthy dependency");
        capsuleNode.Props["example"]!.GetValue<string>().Should().Contain("marks the dependency unhealthy");
        capsuleNode.Props["has_boundary"]!.GetValue<bool>().Should().BeTrue();
        capsuleNode.Props["boundary_text"]!.GetValue<string>().Should().Contain("Do not retry through the breaker");

        var seeAlso = capsuleNode.Props["see_also"] as JsonArray;
        seeAlso.Should().NotBeNull();
        seeAlso!.Select(x => x!.GetValue<string>()).Should().Contain("RetryPolicy");
        seeAlso.Select(x => x!.GetValue<string>()).Should().Contain("Backpressure");
    }

    [Test]
    public async Task Capsule_Headline_Contains_Name_And_Invariant()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/docs/test.md");

        var md = """
            # Test

            ### Capsule: IdempotencyKey

            **Invariant**
            A stable request key makes repeating an operation produce the same effect as doing it once.

            **Depth**
            - NotThis: retries without a key are not idempotent.
            """;

        fs.AddOrUpdateText("docs/test.md", md);
        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var capsuleNode = records.Nodes.Single(n => n.Kind == "md_capsule");

        capsuleNode.Headline.Should().StartWith("Capsule: IdempotencyKey - ");
        capsuleNode.Headline.Should().Contain("stable request key");
    }

    [Test]
    public async Task Capsule_Structure_Excludes_Depth()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/docs/test.md");

        var md = """
            # Test

            ## Capsule: TestCapsule

            **Invariant**
            This is the invariant text.

            **Example**
            This is the example text.
            //BOUNDARY: This is the boundary.

            **Depth**
            - This is a depth bullet that should NOT appear in structure.
            - SeeAlso: `OtherCapsule`.
            """;

        fs.AddOrUpdateText("docs/test.md", md);
        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var capsuleNode = records.Nodes.Single(n => n.Kind == "md_capsule");

        capsuleNode.Structure.Should().Contain("**Invariant**");
        capsuleNode.Structure.Should().Contain("This is the invariant text");
        capsuleNode.Structure.Should().Contain("**Example**");
        capsuleNode.Structure.Should().Contain("This is the example text");
        capsuleNode.Structure.Should().Contain("//BOUNDARY:");
        capsuleNode.Structure.Should().NotContain("depth bullet");
        capsuleNode.Structure.Should().NotContain("SeeAlso");
    }

    [Test]
    public async Task Capsule_Without_Example_Has_Null_Example()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/docs/test.md");

        var md = """
            # Test

            ## Capsule: SimpleRule

            **Invariant**
            Keep it simple.

            **Depth**
            - Trade-off: simplicity vs completeness.
            """;

        fs.AddOrUpdateText("docs/test.md", md);
        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var capsuleNode = records.Nodes.Single(n => n.Kind == "md_capsule");

        capsuleNode.Props!["name"]!.GetValue<string>().Should().Be("SimpleRule");
        capsuleNode.Props["invariant"]!.GetValue<string>().Should().Be("Keep it simple.");
        capsuleNode.Props["example"].Should().BeNull();
        capsuleNode.Props["has_boundary"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public async Task Multiple_Capsules_Creates_RefersTo_Edges_For_SeeAlso()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/docs/test.md");

        var md = """
            # Concepts

            ## Capsule: CapsuleA

            **Invariant**
            First capsule.

            **Depth**
            - SeeAlso: `CapsuleB`.

            ## Capsule: CapsuleB

            **Invariant**
            Second capsule referenced by A.

            **Depth**
            - SeeAlso: `CapsuleA`.
            """;

        fs.AddOrUpdateText("docs/test.md", md);
        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var capsuleA = records.Nodes.Single(n => n.Kind == "md_capsule" && n.Props!["name"]!.GetValue<string>() == "CapsuleA");
        var capsuleB = records.Nodes.Single(n => n.Kind == "md_capsule" && n.Props!["name"]!.GetValue<string>() == "CapsuleB");

        // A should reference B
        var aToB = records.Edges.Where(e => e.SrcId == capsuleA.Id && e.DstId == capsuleB.Id && e.Type == "REFERS_TO");
        aToB.Should().HaveCount(1);

        // B should reference A
        var bToA = records.Edges.Where(e => e.SrcId == capsuleB.Id && e.DstId == capsuleA.Id && e.Type == "REFERS_TO");
        bToA.Should().HaveCount(1);
    }

    [Test]
    public async Task Document_Structure_Shows_Capsules()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.CapsuleStructure";
            options.AddMarkdownFormat();
        });

        var md = """
            # Documentation

            ## Overview
            Some intro text.

            ## Capsule: CircuitBreaker

            **Invariant**
            Stop calls to an unhealthy dependency until recovery is shown.

            **Example**
            Test example.

            **Depth**
            - Details here.
            """;

        var uri = repo.AddOrUpdateText("docs/concepts.md", md);
        await repo.IndexAsync();

        var doc = await repo.WaitForDocumentAsync(uri, DefaultTimeout) ?? throw new TimeoutException("Document was not indexed");
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Structure.Should().Contain("Capsules:");
        artifact.Structure.Should().Contain("CircuitBreaker:");
        artifact.Structure.Should().Contain("Stop calls to an unhealthy dependency");
    }

    [Test]
    public async Task Capsule_Has_HAS_PART_Edge_From_Document()
    {
        var loader = new Formats.Markdown.MarkdownLoader();
        var fs = new MemoryFileSystem("repo");
        var uri = RepoUri.Parse("mem://repo/docs/test.md");

        var md = """
            # Test

            ## Capsule: TestCapsule

            **Invariant**
            Test invariant.

            **Depth**
            - Test.
            """;

        fs.AddOrUpdateText("docs/test.md", md);
        var artifact = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(artifact);
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        var capsuleNode = records.Nodes.Single(n => n.Kind == "md_capsule");

        var hasPart = records.Edges.Where(e =>
            e.SrcId == docNode.Id &&
            e.DstId == capsuleNode.Id &&
            e.Type == "HAS_PART" &&
            e.IsComposition);

        hasPart.Should().HaveCount(1);
    }
}
