using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustImplDissolutionTests
{
    [Test]
    public async Task ImplDissolution_SameFileMethodsParentToType_CrossFileMethodsParentToStubType()
    {
        const string source = """
            pub struct Cache;

            impl Cache {
                pub fn new() -> Self {
                    Cache
                }
            }

            impl Cache {
                pub fn with_value(&self) -> i32 {
                    1
                }
            }

            impl RemoteType {
                pub fn ping(&self) {}
            }
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("impls.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        var cacheType = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Cache");
        var remoteStub = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "RemoteType");

        var newMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "new");
        var withValueMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "with_value");
        var pingMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "ping");

        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == cacheType.Id && e.DstId == newMethod.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == cacheType.Id && e.DstId == withValueMethod.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == docNode.Id && e.DstId == remoteStub.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == remoteStub.Id && e.DstId == pingMethod.Id);
        records.Edges.Should().NotContain(e => e.Type == "HAS_PART" && e.SrcId == docNode.Id && e.DstId == pingMethod.Id);

        newMethod.Props["declaring_type"]!.ToString().Should().Be("Cache");
        withValueMethod.Props["declaring_type"]!.ToString().Should().Be("Cache");
        pingMethod.Props["declaring_type"]!.ToString().Should().Be("RemoteType");
        remoteStub.Props["is_stub"]!.GetValue<bool>().Should().BeTrue();

        newMethod.Props["is_static"]!.GetValue<bool>().Should().BeTrue();
        withValueMethod.Props["is_static"]!.GetValue<bool>().Should().BeFalse();
        pingMethod.Props["is_static"]!.GetValue<bool>().Should().BeFalse();
    }
}
