using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustCrossFileImplTests
{
    [Test]
    public async Task Materialize_CrossFileImpls_CreateStubTypeNodeAndParentMethodsToStub()
    {
        const string source = """
            pub struct Local;

            impl RemoteType {
                pub fn ping(&self) {}
            }

            impl RemoteType {
                pub fn pong(&self) {}
            }
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("cross_file_impl.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        var localType = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Local");
        var remoteStub = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "RemoteType");
        var pingMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "ping");
        var pongMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "pong");

        localType.Props["is_stub"]!.GetValue<bool>().Should().BeFalse();
        remoteStub.Props["is_stub"]!.GetValue<bool>().Should().BeTrue();
        remoteStub.Props["kind"]!.ToString().Should().Be("struct");

        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == docNode.Id && e.DstId == remoteStub.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == remoteStub.Id && e.DstId == pingMethod.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.SrcId == remoteStub.Id && e.DstId == pongMethod.Id);
        records.Edges.Should().NotContain(e => e.Type == "HAS_PART" && e.SrcId == docNode.Id && e.DstId == pingMethod.Id);
        records.Edges.Should().NotContain(e => e.Type == "HAS_PART" && e.SrcId == docNode.Id && e.DstId == pongMethod.Id);

        pingMethod.Props["declaring_type"]!.ToString().Should().Be("RemoteType");
        pongMethod.Props["declaring_type"]!.ToString().Should().Be("RemoteType");
    }
}
