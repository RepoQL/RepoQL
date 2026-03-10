using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

public sealed class InferenceReadToolDefinitionFactoryTests
{
    [Test]
    public void Create_ProducesReadToolDefinitionWithStrippedQuestionModifier()
    {
        var definition = InferenceReadToolDefinitionFactory.Create();

        definition.Name.Should().Be("read");
        definition.Description.Should().NotContain("=> question:");
        definition.Description.Should().NotContain("**question**");
        definition.Description.Should().Contain("**tree**");
        definition.Description.Should().Contain("**history**");
        definition.Description.Should().Contain("**blame**");

        var parameters = JsonNode.Parse(definition.ParametersJson).Should().BeOfType<JsonObject>().Subject;
        parameters["type"]!.GetValue<string>().Should().Be("object");
        parameters["properties"].Should().BeOfType<JsonObject>();
        parameters["properties"]!.AsObject().Should().NotBeEmpty();
    }

    [Test]
    public void StripQuestionModifierDocumentation_RemovesQuestionBlock()
    {
        const string description = """
            <MODIFIERS>
            **tree**: Show tree output.
            **question**: Ask a focused question about specific code.
            → `=> question: How does X work?`
            Returns synthesized answer.
            **history**: Show git history.
            </MODIFIERS>
            <AFTER>
            Still here.
            </AFTER>
            """;

        var stripped = InferenceReadToolDefinitionFactory.StripQuestionModifierDocumentation(description);

        stripped.Should().Contain("**tree**");
        stripped.Should().Contain("<AFTER>");
        stripped.Should().NotContain("**question**");
        stripped.Should().NotContain("=> question:");
        stripped.Should().NotContain("Ask a focused question");
    }
}
