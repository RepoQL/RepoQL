using AwesomeAssertions;
using RepoQL.Formats.Go.TreeSitter;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoEnumPatternTests
{
    [Test]
    public void Parse_EnumPattern_DetectsIotaEnumBlock()
    {
        using var client = new GoTreeSitterClient();
        var source = GoTestHelpers.ReadFixture("enum_pattern.go");

        var result = client.Parse(source);

        result.Constants.Select(c => c.Name).Should().Contain(["ColorRed", "ColorBlue", "ColorGreen", "StatusOK"]);
        result.Constants.Should().Contain(c => c.Name == "ColorRed" && c.TypeName == "Color");
        result.Constants.Should().Contain(c => c.Name == "ColorBlue" && c.TypeName == "Color");
        result.Constants.Should().Contain(c => c.Name == "ColorGreen" && c.TypeName == "Color");

        var colorBlock = result.ConstantBlocks.Single(b => b.TypeName == "Color");
        colorBlock.HasIota.Should().BeTrue();
        colorBlock.Constants.Select(c => c.Name).Should().ContainInOrder(["ColorRed", "ColorBlue", "ColorGreen"]);

        result.ConstantBlocks.Should().Contain(b => !b.HasIota);
    }

    [Test]
    public async Task Materialize_EnumPattern_EmitsEnumAnnotationAndEnumTypeProperties()
    {
        var records = await GoTestHelpers.LoadRecordsAsync("enum_pattern.go");

        var enumAnnotation = records.Annotations.Single(a => a.Kind == "go.enum_block");
        enumAnnotation.Data["type_name"]!.ToString().Should().Be("Color");
        enumAnnotation.Data["constant_count"]!.GetValue<int>().Should().Be(3);

        var colorConstants = records.Nodes
            .Where(n => n.Kind == "go.member" && n.Props["kind"]!.ToString() == "constant")
            .Where(n => n.Props["name"]!.ToString().StartsWith("Color", StringComparison.Ordinal))
            .ToList();

        colorConstants.Should().HaveCount(3);
        colorConstants.Should().OnlyContain(n => n.Props["enum_type"]!.ToString() == "Color");
    }
}

