using AwesomeAssertions;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class CoreTypesTests
{
    [Test]
    [DisplayName("Intent enum has expected values")]
    public void Intent_HasExpectedValues()
    {
        Enum.GetValues<Intent>().Should().BeEquivalentTo([Intent.Explore, Intent.Find, Intent.Examine]);
    }

    [Test]
    [DisplayName("Representation enum has expected values in order")]
    public void Representation_HasExpectedValuesInOrder()
    {
        var values = Enum.GetValues<Representation>();

        values.Should().BeEquivalentTo([
            Representation.Minimal,
            Representation.Compact,
            Representation.Standard,
            Representation.Rich
        ]);

        // Verify order (important for degradation logic)
        ((int)Representation.Minimal).Should().BeLessThan((int)Representation.Compact);
        ((int)Representation.Compact).Should().BeLessThan((int)Representation.Standard);
        ((int)Representation.Standard).Should().BeLessThan((int)Representation.Rich);
    }

    [Test]
    [DisplayName("XrayResult can be constructed with all fields")]
    public void XrayResult_CanBeConstructedWithAllFields()
    {
        var result = new XrayResult(
            Uri: "file:///test.cs#line=10,20",
            Confidence: 95,
            Kind: "method",
            Headline: "Test method",
            Structure: "- Method body",
            Snippet: "public void Test() { }",
            Lang: "csharp"
        );

        result.Uri.Should().Be("file:///test.cs#line=10,20");
        result.Confidence.Should().Be(95);
        result.Kind.Should().Be("method");
        result.Headline.Should().Be("Test method");
        result.Structure.Should().Be("- Method body");
        result.Snippet.Should().Be("public void Test() { }");
        result.Lang.Should().Be("csharp");
    }

    [Test]
    [DisplayName("XrayResult can be constructed with nullable fields as null")]
    public void XrayResult_CanHaveNullFields()
    {
        var result = new XrayResult(
            Uri: "file:///test.cs",
            Confidence: 50,
            Kind: null,
            Headline: null,
            Structure: null,
            Snippet: null,
            Lang: null
        );

        result.Kind.Should().BeNull();
        result.Headline.Should().BeNull();
        result.Structure.Should().BeNull();
        result.Snippet.Should().BeNull();
        result.Lang.Should().BeNull();
    }

    [Test]
    [DisplayName("XrayResult records are equal when fields match")]
    public void XrayResult_RecordEquality()
    {
        var result1 = new XrayResult("file:///a.cs", 80, "class", "Headline", null, null, null);
        var result2 = new XrayResult("file:///a.cs", 80, "class", "Headline", null, null, null);

        result1.Should().Be(result2);
        (result1 == result2).Should().BeTrue();
    }

    [Test]
    [DisplayName("RenderingContext can be constructed")]
    public void RenderingContext_CanBeConstructed()
    {
        var context = new RenderingContext(
            Intent: Intent.Find,
            TokenBudget: 2000,
            Limit: 50,
            HasSearchCriteria: true
        );

        context.Intent.Should().Be(Intent.Find);
        context.TokenBudget.Should().Be(2000);
        context.Limit.Should().Be(50);
        context.HasSearchCriteria.Should().BeTrue();
    }

    [Test]
    [DisplayName("RenderingContext limit can be null")]
    public void RenderingContext_LimitCanBeNull()
    {
        var context = new RenderingContext(Intent.Explore, 1000, null, false);

        context.Limit.Should().BeNull();
    }

    [Test]
    [DisplayName("RenderingDecision can be constructed")]
    public void RenderingDecision_CanBeConstructed()
    {
        var result = ResultBuilder.Create(85);
        var decision = new RenderingDecision(result, Representation.Compact, 50);

        decision.Result.Should().Be(result);
        decision.Level.Should().Be(Representation.Compact);
        decision.EstimatedTokens.Should().Be(50);
    }

    [Test]
    [DisplayName("ResultBuilder creates document with expected fields")]
    public void ResultBuilder_CreatesDocument()
    {
        var doc = ResultBuilder.Document(75, headlineLength: 100, structureLength: 200);

        doc.Confidence.Should().Be(75);
        doc.Kind.Should().BeNull("documents have no kind");
        doc.Headline.Should().HaveLength(100);
        doc.Structure.Should().HaveLength(200);
        doc.Snippet.Should().BeNull();
    }

    [Test]
    [DisplayName("ResultBuilder creates object with expected fields")]
    public void ResultBuilder_CreatesObject()
    {
        var obj = ResultBuilder.ObjectResult(90, kind: "class", snippetLength: 300);

        obj.Confidence.Should().Be(90);
        obj.Kind.Should().Be("class");
        obj.Snippet.Should().HaveLength(300);
        obj.Lang.Should().Be("csharp");
    }
}
