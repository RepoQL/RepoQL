using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustAttributeTests
{
    [Test]
    public async Task Materialize_StructuredAttributes_AppliesExpectedPropertiesAndAttributesArray()
    {
        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact(
            "structured_attributes.rs",
            FixtureReader.Read("structured_attributes.rs"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var compute = records.Nodes.Single(n => n.Kind == "rs.function" && n.Props["name"]!.ToString() == "compute");
        compute.Props["cfg"]!.ToString().Should().Be("feature = \"feat_x\"");
        compute.Props["is_inline"]!.GetValue<bool>().Should().BeTrue();
        compute.Props["must_use"]!.GetValue<bool>().Should().BeTrue();
        compute.Props["is_deprecated"]!.GetValue<bool>().Should().BeTrue();

        var computeAttributesJson = compute.Props["attributes"]!.ToJsonString();
        computeAttributesJson.Should().Contain("\"name\":\"cfg\"");
        computeAttributesJson.Should().Contain("\"name\":\"inline\"");
        computeAttributesJson.Should().Contain("\"name\":\"must_use\"");
        computeAttributesJson.Should().Contain("\"name\":\"deprecated\"");

        var smoke = records.Nodes.Single(n => n.Kind == "rs.function" && n.Props["name"]!.ToString() == "smoke");
        smoke.Props["is_test"]!.GetValue<bool>().Should().BeTrue();
        smoke.Props["attributes"]!.ToJsonString().Should().Contain("\"name\":\"test\"");

        var testSupport = records.Nodes.Single(n => n.Kind == "rs.module" && n.Props["name"]!.ToString() == "test_support");
        testSupport.Props["cfg"]!.ToString().Should().Be("test");
        testSupport.Props["attributes"]!.ToJsonString().Should().Contain("\"name\":\"cfg\"");
    }
}
