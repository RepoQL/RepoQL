using System.Reflection;
using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustSharedViewTests
{
    [Test]
    public async Task MaterializedRustNodeKindsAndProperties_MatchSharedViewContracts()
    {
        const string source = """
            pub struct Worker;

            impl Worker {
                pub fn run(&self) {}
            }

            pub fn build() {}
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("shared.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Should().Contain(n => n.Kind == "rs.type" && n.Props["kind"]!.ToString() == "struct");
        records.Nodes.Should().Contain(n => n.Kind == "rs.member" && n.Props["kind"]!.ToString() == "method");
        records.Nodes.Should().Contain(n => n.Kind == "rs.function" && n.Props["kind"]!.ToString() == "function");

        records.Nodes.Single(n => n.Kind == "rs.member").Props["parameters"].Should().NotBeNull();
        records.Nodes.Single(n => n.Kind == "rs.function").Props["is_static"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void SharedViewSql_IncludesRustKindsAndTypePattern()
    {
        var typesSql = ReadEmbeddedViewSql("types.sql");
        var functionsSql = ReadEmbeddedViewSql("functions.sql");

        typesSql.Should().Contain("WHERE n.kind LIKE '%.type'");

        functionsSql.Should().Contain("'rs.member'");
        functionsSql.Should().Contain("'rs.function'");
        functionsSql.Should().Contain("json_extract_string(n.properties, '$.kind') IN ('method', 'constructor', 'function')");
    }

    private static string ReadEmbeddedViewSql(string fileName)
    {
        var assembly = typeof(RepoQL.Data.DuckDB.DuckDbDataStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"Views.{fileName}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded resource ending in 'Views.{fileName}' not found in {assembly.GetName().Name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
