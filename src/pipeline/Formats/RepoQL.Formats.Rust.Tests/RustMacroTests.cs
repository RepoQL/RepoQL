using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustMacroTests
{
    [Test]
    public async Task Materialize_MacroDefinitionsInvocationsAndProcMacroAttributes_CreateHonestySurfaceWithNoiseFiltering()
    {
        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact(
            "macros_and_proc_attributes.rs",
            FixtureReader.Read("macros_and_proc_attributes.rs"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        var macroNode = records.Nodes.Single(n => n.Kind == "rs.macro" && n.Props["name"]!.ToString() == "make_value");

        macroNode.Props["qualified_name"]!.ToString().Should().Be("make_value");
        macroNode.Props["accessibility"]!.ToString().Should().Be("private");
        records.Edges.Should().Contain(e =>
            e.Type == "HAS_PART"
            && e.IsComposition
            && e.SrcId == docNode.Id
            && e.DstId == macroNode.Id);

        var macroExpansion = records.Annotations
            .Where(a => a.Kind == "rs.macro_expansion")
            .ToArray();

        macroExpansion.Select(a => a.RuleId).Should().Contain("derive");
        macroExpansion.Select(a => a.RuleId).Should().Contain("make_value");
        macroExpansion.Select(a => a.RuleId).Should().Contain("lazy_static");
        macroExpansion.Select(a => a.RuleId).Should().Contain("tokio::main");
        macroExpansion.Select(a => a.RuleId).Should().Contain("async_trait");

        macroExpansion.Select(a => a.RuleId).Should().NotContain("println");
        macroExpansion.Select(a => a.RuleId).Should().NotContain("vec");
        macroExpansion.Select(a => a.RuleId).Should().NotContain("allow");
        macroExpansion.Select(a => a.RuleId).Should().NotContain("cfg");
        macroExpansion.Select(a => a.RuleId).Should().NotContain("inline");
        macroExpansion.Select(a => a.RuleId).Should().NotContain("must_use");
        macroExpansion.Select(a => a.RuleId).Should().NotContain("deprecated");

        macroExpansion.Count(a => a.RuleId == "derive").Should().Be(1);
        macroExpansion.All(a => a.ScopeDocumentId == docNode.Id).Should().BeTrue();
        macroExpansion.All(a => a.TargetSpanId is not null).Should().BeTrue();
        macroExpansion.All(a => a.Message.Contains("not captured", StringComparison.Ordinal)).Should().BeTrue();

        foreach (var annotation in macroExpansion)
        {
            records.Spans.Should().Contain(span => span.Id == annotation.TargetSpanId);
        }
    }

    [Test]
    public void SchemaScripts_IncludeRustImportMacroAndMacroExpansionViews()
    {
        using var loader = new RustLoader();

        var scripts = loader.GetSchemaScripts().ToList();

        scripts.Should().ContainSingle(s => s.Identifier == "rust_views");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW rust_imports");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW rust_macros");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW rust_macro_expansion");
        scripts[0].Sql.Should().Contain("AS import_path");
        scripts[0].Sql.Should().Contain("AS is_reexport");
        scripts[0].Sql.Should().Contain("AS macro_uri");
        scripts[0].Sql.Should().Contain("AS macro_name");
    }
}
