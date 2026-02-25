using AwesomeAssertions;
using RepoQL.Formats.Go.GoMod;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoModParserTests
{
    [Test]
    public void Parse_MinimalGoMod_ExtractsModuleAndGoVersion()
    {
        var parser = new GoModParser();

        var info = parser.Parse(GoTestHelpers.ReadFixture("go_mod_minimal.mod"));

        info.ModulePath.Should().Be("github.com/example/minimal");
        info.GoVersion.Should().Be("1.22");
        info.Toolchain.Should().BeNull();
        info.Requirements.Should().BeEmpty();
        info.Replacements.Should().BeEmpty();
        info.Retractions.Should().BeEmpty();
    }

    [Test]
    public void Parse_GoMod_ExtractsDirectAndIndirectDependencies()
    {
        var parser = new GoModParser();

        var info = parser.Parse(GoTestHelpers.ReadFixture("go.mod"));

        info.Requirements.Should().HaveCount(3);
        info.Requirements.Should().Contain(r =>
            r.ModulePath == "github.com/gin-gonic/gin"
            && r.Version == "v1.10.0"
            && !r.IsIndirect);
        info.Requirements.Should().Contain(r =>
            r.ModulePath == "github.com/stretchr/testify"
            && r.Version == "v1.9.0"
            && r.IsIndirect);
    }

    [Test]
    public void Parse_GoMod_ExtractsReplaceDirectivesIncludingLocalPath()
    {
        var parser = new GoModParser();

        var info = parser.Parse(GoTestHelpers.ReadFixture("go.mod"));

        info.Replacements.Should().HaveCount(2);
        info.Replacements.Should().Contain(r =>
            r.OldPath == "github.com/acme/common"
            && r.OldVersion == null
            && r.NewPath == "../common"
            && r.NewVersion == null
            && r.IsLocalPath);
        info.Replacements.Should().Contain(r =>
            r.OldPath == "example.com/fork"
            && r.OldVersion == "v1.2.3"
            && r.NewPath == "github.com/acme/fork"
            && r.NewVersion == "v1.2.4"
            && !r.IsLocalPath);
    }

    [Test]
    public void Parse_GoMod_MixedSingleLineAndBlocks_ExtractsExpectedMetadata()
    {
        var parser = new GoModParser();

        var info = parser.Parse(GoTestHelpers.ReadFixture("go_mod_complex.mod"));

        info.ModulePath.Should().Be("github.com/acme/complex");
        info.GoVersion.Should().Be("1.23");
        info.Toolchain.Should().Be("go1.23.0");
        info.Requirements.Should().HaveCount(3);
        info.Requirements.Should().Contain(r => r.ModulePath == "github.com/pkg/errors" && !r.IsIndirect);
        info.Requirements.Should().Contain(r => r.ModulePath == "github.com/google/uuid" && r.IsIndirect);
        info.Replacements.Should().HaveCount(2);
        info.Retractions.Should().HaveCount(2);
    }

    [Test]
    public void Parse_GoWork_ExtractsUsesAndReplacements()
    {
        var parser = new GoModParser();

        var info = parser.Parse(GoTestHelpers.ReadFixture("go.work"));

        info.ModulePath.Should().BeNull();
        info.GoVersion.Should().Be("1.22");
        info.Toolchain.Should().Be("go1.22.3");
        info.Uses.Select(u => u.Path).Should().ContainInOrder(["./cmd/api", "./pkg/shared", "./tools"]);
        info.Replacements.Should().HaveCount(2);
    }

    [Test]
    public void Parse_MalformedInput_ReturnsPartialResults()
    {
        var parser = new GoModParser();
        var text = """
                   module github.com/example/broken
                   go 1.22
                   require (
                     github.com/ok/mod v1.0.0
                     this-is-not-valid
                   )
                   replace broken
                   replace github.com/old/mod => ../local/mod
                   use ./workspace-a
                   """;

        var info = parser.Parse(text);

        info.ModulePath.Should().Be("github.com/example/broken");
        info.GoVersion.Should().Be("1.22");
        info.Requirements.Should().ContainSingle(r => r.ModulePath == "github.com/ok/mod" && r.Version == "v1.0.0");
        info.Replacements.Should().ContainSingle(r => r.OldPath == "github.com/old/mod" && r.NewPath == "../local/mod");
        info.Uses.Should().ContainSingle(u => u.Path == "./workspace-a");
    }

    [Test]
    public async Task ParseAndMaterialize_GoMod_EmitsDependsOnReferenceEdges()
    {
        var parser = new GoModParser();
        var source = GoTestHelpers.ReadFixture("go.mod");
        var parsed = parser.Parse(source);

        var records = await GoTestHelpers.LoadRecordsAsync("go.mod");

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["language"]!.ToString().Should().Be("go.mod");
        docNode.Props["module_path"]!.ToString().Should().Be("github.com/acme/service");
        docNode.Props["go_version"]!.ToString().Should().Be("1.22");
        docNode.Props["toolchain"]!.ToString().Should().Be("go1.22.1");

        var dependencies = records.Edges.Where(e => e.Type == "DEPENDS_ON").ToList();
        dependencies.Should().HaveCount(parsed.Requirements.Count);
        dependencies.Should().OnlyContain(e => !e.IsComposition && e.DstId == null);
        dependencies.Should().Contain(e =>
            e.Props["target"]!.ToString() == "github.com/stretchr/testify"
            && e.Props["version"]!.ToString() == "v1.9.0"
            && e.Props["indirect"]!.GetValue<bool>());

        records.Annotations.Should().Contain(a => a.Kind == "go.mod_replace");
        records.Annotations.Should().Contain(a => a.Kind == "go.mod_retract");
        records.Artifacts[0].Headline.Should().Contain("go.mod | code.go.mod");
        records.Artifacts[0].Headline.Should().Contain("module:github.com/acme/service");
        records.Artifacts[0].Headline.Should().Contain("2 direct, 1 indirect deps");
    }

    [Test]
    public async Task Materialize_GoWork_EmitsUseAndReplaceAnnotations()
    {
        var records = await GoTestHelpers.LoadRecordsAsync("go.work");

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["language"]!.ToString().Should().Be("go.work");
        docNode.Props["go_version"]!.ToString().Should().Be("1.22");
        docNode.Props["toolchain"]!.ToString().Should().Be("go1.22.3");
        records.Edges.Should().NotContain(e => e.Type == "DEPENDS_ON");
        records.Annotations.Where(a => a.Kind == "go.work_use").Should().HaveCount(3);
        records.Annotations.Should().Contain(a => a.Kind == "go.mod_replace");
        records.Artifacts[0].Headline.Should().Contain("go.work | code.go.work");
        records.Artifacts[0].Headline.Should().Contain("3 workspace modules");
    }
}
