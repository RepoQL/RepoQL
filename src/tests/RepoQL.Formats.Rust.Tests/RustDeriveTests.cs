using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustDeriveTests
{
    [Test]
    public async Task Materialize_Derives_CreateDeriveEdgesAndMacroExpansionAnnotation()
    {
        const string source = """
            #[derive(Debug, Clone, serde::Serialize)]
            pub struct Config {
                value: i32,
            }
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("derives.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        var configType = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Config");

        configType.Props["derives"]!.ToString().Should().Contain("Debug");
        configType.Props["derives"]!.ToString().Should().Contain("Clone");
        configType.Props["derives"]!.ToString().Should().Contain("serde::Serialize");

        var deriveEdges = records.Edges
            .Where(e => e.Type == "DERIVES" && e.SrcId == configType.Id)
            .ToArray();
        deriveEdges.Should().HaveCount(3);
        deriveEdges.Select(e => e.Props["target"]!.ToString())
            .Should()
            .BeEquivalentTo(["Debug", "Clone", "serde::Serialize"]);
        deriveEdges.All(e => !e.IsComposition && e.DstId is null).Should().BeTrue();

        var deriveAnnotation = records.Annotations.Single(a => a.Kind == "rs.macro_expansion" && a.RuleId == "derive");
        deriveAnnotation.ScopeDocumentId.Should().Be(docNode.Id);
        deriveAnnotation.TargetSpanId.Should().NotBeNull();
        deriveAnnotation.Message.Should().Contain("Debug");
        deriveAnnotation.Message.Should().Contain("Clone");
        deriveAnnotation.Message.Should().Contain("serde::Serialize");
        deriveAnnotation.Message.Should().Contain("generated impl blocks are not captured");
        records.Spans.Should().Contain(span => span.Id == deriveAnnotation.TargetSpanId);
    }
}
