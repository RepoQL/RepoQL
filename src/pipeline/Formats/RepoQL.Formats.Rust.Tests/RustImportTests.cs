using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustImportTests
{
    [Test]
    public async Task Materialize_UseDeclarations_CreateImportEdgesWithExpansionAliasGlobAndReexportMetadata()
    {
        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact(
            "imports_materialization.rs",
            FixtureReader.Read("imports_materialization.rs"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var importEdges = records.Edges
            .Where(e => e.Type == "IMPORTS")
            .ToArray();

        importEdges.Should().HaveCount(6);
        importEdges.All(e => !e.IsComposition && e.DstId is null).Should().BeTrue();

        importEdges.Select(e => e.Props["path"]!.ToString())
            .Should()
            .BeEquivalentTo([
                "std::fs",
                "std::io",
                "std::collections::HashMap",
                "std::io::*",
                "crate::models::Account",
                "crate::models::User"
            ]);

        var aliasImport = importEdges.Single(e => e.Props["path"]!.ToString() == "std::collections::HashMap");
        aliasImport.Props["alias"]!.ToString().Should().Be("Map");
        aliasImport.Props["is_glob"]!.ToString().Should().Be("false");
        aliasImport.Props["is_pub"]!.ToString().Should().Be("false");

        var globImport = importEdges.Single(e => e.Props["path"]!.ToString() == "std::io::*");
        globImport.Props["is_glob"]!.ToString().Should().Be("true");
        globImport.Props["is_pub"]!.ToString().Should().Be("false");

        var reexports = importEdges
            .Where(e => e.Props["is_pub"]!.ToString() == "true")
            .ToArray();
        reexports.Should().HaveCount(2);
        reexports.Select(e => e.Props["path"]!.ToString()).Should().BeEquivalentTo([
            "crate::models::Account",
            "crate::models::User"
        ]);

        var accountReexport = importEdges.Single(e => e.Props["path"]!.ToString() == "crate::models::Account");
        accountReexport.Props["alias"]!.ToString().Should().Be("Acct");

        var userReexport = importEdges.Single(e => e.Props["path"]!.ToString() == "crate::models::User");
        userReexport.Props["alias"].Should().BeNull();
    }
}
