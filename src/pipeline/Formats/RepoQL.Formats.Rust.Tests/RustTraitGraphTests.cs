using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustTraitGraphTests
{
    [Test]
    public async Task Materialize_TraitImplsAndSupertraits_CreateImplementsAndExtendsReferenceEdges()
    {
        const string source = """
            pub trait Read: BufRead + Seek {}

            pub struct Cache;

            unsafe impl Display for Cache {}
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("trait_graph.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var cacheType = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Cache");
        var readTrait = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Read");

        var implementsEdge = records.Edges.Single(e => e.Type == "IMPLEMENTS" && e.SrcId == cacheType.Id);
        implementsEdge.IsComposition.Should().BeFalse();
        implementsEdge.DstId.Should().BeNull();
        implementsEdge.Props["target"]!.ToString().Should().Be("Display");
        implementsEdge.Props["is_unsafe"]!.ToString().Should().Be("true");

        var extendsEdges = records.Edges
            .Where(e => e.Type == "EXTENDS" && e.SrcId == readTrait.Id)
            .ToArray();
        extendsEdges.Should().HaveCount(2);
        extendsEdges.Select(e => e.Props["target"]!.ToString()).Should().BeEquivalentTo(["BufRead", "Seek"]);
        extendsEdges.All(e => !e.IsComposition && e.DstId is null).Should().BeTrue();
    }
}
