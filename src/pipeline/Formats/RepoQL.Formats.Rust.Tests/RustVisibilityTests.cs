using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustVisibilityTests
{
    [Test]
    public async Task VisibilityNormalization_MapsAllRustVisibilityForms()
    {
        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("visibility.rs", FixtureReader.Read("visibility_modifiers.rs"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var accessByName = records.Nodes
            .Where(n => n.Kind == "rs.type")
            .ToDictionary(
                n => n.Props["name"]!.ToString(),
                n => n.Props["accessibility"]!.ToString(),
                StringComparer.Ordinal);

        accessByName["PublicType"].Should().Be("public");
        accessByName["CrateType"].Should().Be("pub_crate");
        accessByName["SuperType"].Should().Be("pub_super");
        accessByName["PathType"].Should().Be("pub_in:crate::outer");
        accessByName["PrivateType"].Should().Be("private");
    }
}
