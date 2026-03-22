using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustUnsafeViewTests
{
    [Test]
    public async Task Materialize_UnsafeFunctionTraitMethodAndImpl_AreFlaggedForUnsafeView()
    {
        const string source = """
            pub unsafe trait Dangerous {}

            pub struct Worker;

            pub unsafe fn raw() {}

            impl Worker {
                pub unsafe fn touch(&self) {}
            }

            unsafe impl Dangerous for Worker {}
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("unsafe.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var dangerousTrait = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Dangerous");
        dangerousTrait.Props["kind"]!.ToString().Should().Be("trait");
        dangerousTrait.Props["is_unsafe"]!.GetValue<bool>().Should().BeTrue();

        var rawFunction = records.Nodes.Single(n => n.Kind == "rs.function" && n.Props["name"]!.ToString() == "raw");
        rawFunction.Props["is_unsafe"]!.GetValue<bool>().Should().BeTrue();

        var touchMethod = records.Nodes.Single(n => n.Kind == "rs.member" && n.Props["name"]!.ToString() == "touch");
        touchMethod.Props["is_unsafe"]!.GetValue<bool>().Should().BeTrue();

        var workerType = records.Nodes.Single(n => n.Kind == "rs.type" && n.Props["name"]!.ToString() == "Worker");
        var implEdge = records.Edges.Single(e => e.Type == "IMPLEMENTS" && e.SrcId == workerType.Id);
        implEdge.Props["target"]!.ToString().Should().Be("Dangerous");
        implEdge.Props["is_unsafe"]!.ToString().Should().Be("true");
    }

    [Test]
    public void SchemaScripts_IncludeRustImplDeriveAndUnsafeViews()
    {
        using var loader = new RustLoader();

        var scripts = loader.GetSchemaScripts().ToList();

        scripts.Should().ContainSingle(s => s.Identifier == "rust_views");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW rust_impls");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW rust_derives");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW rust_unsafe");
        scripts[0].Sql.Should().Contain("COUNT(DISTINCT doc.uri) AS definition_count");
        scripts[0].Sql.Should().Contain("LIST(DISTINCT doc.uri) AS defined_in");

        scripts[0].Sql.Should().Contain("FROM rust_functions");
        scripts[0].Sql.Should().Contain("FROM rust_methods");
        scripts[0].Sql.Should().Contain("FROM rust_types");
        scripts[0].Sql.Should().Contain("FROM rust_impls");
    }
}
