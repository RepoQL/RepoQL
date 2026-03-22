using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustLoaderTests
{
    [Test]
    public async Task LoadAndMaterialize_RoundTrip_ProducesRustNodesEdgesAndSpans()
    {
        const string source = """
            #[derive(Debug, Clone)]
            pub struct Cache<T> {
                pub value: T,
                count: usize,
            }

            impl<T> Cache<T> {
                pub fn new(value: T) -> Self {
                    Self { value, count: 0 }
                }

                pub async fn refresh(&mut self) -> Result<(), String> {
                    Ok(())
                }
            }

            pub fn make_cache() -> Cache<i32> {
                Cache::new(1)
            }

            pub mod inner {
                pub fn ping() {}
            }
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("sample.rs", source);

        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        artifactScope.Artifact.MediaType!.Kind.Should().Be("code.rust");

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();
        records.Edges.Should().NotBeEmpty();
        records.Spans.Should().NotBeEmpty();

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["language"]!.ToString().Should().Be("rust");
        documentNode.Props["line_count"]!.GetValue<int>().Should().BeGreaterThan(0);
        documentNode.Props["byte_size"]!.GetValue<long>().Should().BeGreaterThan(0);

        var cacheType = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Cache");
        cacheType.Props["kind"]!.ToString().Should().Be("struct");
        cacheType.Props["accessibility"]!.ToString().Should().Be("public");
        cacheType.Props["derives"]!.ToString().Should().Contain("Debug");
        cacheType.Props["fields"]!.ToJsonString().Should().Contain("\"value\"");

        var newMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "new");
        newMethod.Props["kind"]!.ToString().Should().Be("method");
        newMethod.Props["declaring_type"]!.ToString().Should().Be("Cache");
        newMethod.Props["is_static"]!.GetValue<bool>().Should().BeTrue();

        var refreshMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "refresh");
        refreshMethod.Props["is_async"]!.GetValue<bool>().Should().BeTrue();
        refreshMethod.Props["self_kind"]!.ToString().Should().Be("&mut self");
        refreshMethod.Props["is_static"]!.GetValue<bool>().Should().BeFalse();

        var functionNode = records.Nodes.Single(n => n.Kind == "rs.function" && n.Props["name"]!.ToString() == "make_cache");
        functionNode.Props["kind"]!.ToString().Should().Be("function");
        functionNode.Props["is_static"]!.GetValue<bool>().Should().BeTrue();
        functionNode.Props["qualified_name"]!.ToString().Should().Be("make_cache");

        var moduleNode = records.Nodes.Single(n => n.Kind == "rs.module" && n.Props["name"]!.ToString() == "inner");
        moduleNode.Props["is_inline"]!.GetValue<bool>().Should().BeTrue();
        moduleNode.Props["accessibility"]!.ToString().Should().Be("public");

        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.IsComposition && e.SrcId == documentNode.Id && e.DstId == cacheType.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.IsComposition && e.SrcId == cacheType.Id && e.DstId == newMethod.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.IsComposition && e.SrcId == cacheType.Id && e.DstId == refreshMethod.Id);

        records.Spans.All(span => span.StartLine >= 1).Should().BeTrue();
        records.Spans.All(span => span.EndByte >= span.StartByte).Should().BeTrue();
        records.Spans.All(span => span.DocumentId == documentNode.Id).Should().BeTrue();
    }
}
