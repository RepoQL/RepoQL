using AwesomeAssertions;

namespace RepoQL.LLM.Client.Tests;

public class JsonToToonConverterTests
{
    #region Basic Conversion

    [Test]
    public async Task Convert_WithEmptyArray_ReturnsEmpty()
    {
        var result = JsonToToonConverter.Convert("[]");

        result.Should().BeEmpty();
    }

    [Test]
    public async Task Convert_WithNullInput_ReturnsEmpty()
    {
        var result = JsonToToonConverter.Convert(null);

        result.Should().BeEmpty();
    }

    [Test]
    public async Task Convert_WithEmptyString_ReturnsEmpty()
    {
        var result = JsonToToonConverter.Convert("");

        result.Should().BeEmpty();
    }

    [Test]
    public async Task Convert_WithSingleRow_GeneratesHeader()
    {
        var json = """[{"uri":"file:///test.cs","headline":"Test file"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("[1]{uri,headline}:");
    }

    [Test]
    public async Task Convert_WithMultipleRows_CountsRows()
    {
        var json = """
            [
                {"name":"foo"},
                {"name":"bar"},
                {"name":"baz"}
            ]
            """;

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("[3]{name}:");
    }

    #endregion

    #region Value Formatting

    [Test]
    public async Task Convert_WithStringValue_OutputsUnquoted()
    {
        var json = """[{"name":"simple"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("simple");
        result.Should().NotContain("\"simple\"");
    }

    [Test]
    public async Task Convert_WithNumericValue_OutputsAsNumber()
    {
        var json = """[{"count":42}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("42");
    }

    [Test]
    public async Task Convert_WithBooleanValue_OutputsLowercase()
    {
        var json = """[{"enabled":true,"disabled":false}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("true");
        result.Should().Contain("false");
    }

    [Test]
    public async Task Convert_WithNullValue_OutputsNull()
    {
        var json = """[{"value":null}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("null");
    }

    #endregion

    #region Quoting Rules

    [Test]
    public async Task Convert_WithCommaInValue_QuotesValue()
    {
        var json = """[{"msg":"hello, world"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("\"hello, world\"");
    }

    [Test]
    public async Task Convert_WithColonInValue_QuotesValue()
    {
        var json = """[{"uri":"file:///test"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("\"file:///test\"");
    }

    [Test]
    public async Task Convert_WithReservedWordTrue_QuotesValue()
    {
        var json = """[{"name":"true"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("\"true\"");
    }

    [Test]
    public async Task Convert_WithLeadingWhitespace_QuotesValue()
    {
        var json = """[{"name":" spaced"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("\" spaced\"");
    }

    [Test]
    public async Task Convert_WithNewline_EscapesAndQuotes()
    {
        var json = """[{"msg":"line1\nline2"}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("\"line1\\nline2\"");
    }

    #endregion

    #region Nested Structures

    [Test]
    public async Task Convert_WithNestedObject_OutputsAsJson()
    {
        var json = """[{"data":{"nested":"value"}}]""";

        var result = JsonToToonConverter.Convert(json);

        // Nested objects stay as compact JSON
        result.Should().Contain("{\"nested\":\"value\"}");
    }

    [Test]
    public async Task Convert_WithArray_OutputsAsJson()
    {
        var json = """[{"items":[1,2,3]}]""";

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("[1,2,3]");
    }

    #endregion

    #region Real-World Scenarios

    [Test]
    public async Task Convert_WithSearchResults_FormatsCorrectly()
    {
        var json = """
            [
                {"uri":"file:///src/Auth.cs","headline":"Authentication service","score":0.95},
                {"uri":"file:///src/Login.cs","headline":"Login handler","score":0.87}
            ]
            """;

        var result = JsonToToonConverter.Convert(json);

        result.Should().Contain("[2]{uri,headline,score}:");
        result.Should().Contain("\"file:///src/Auth.cs\"");
        result.Should().Contain("Authentication service");
        result.Should().Contain("0.95");
    }

    [Test]
    public async Task Convert_WithInvalidJson_ReturnsInput()
    {
        var invalidJson = "not valid json";

        var result = JsonToToonConverter.Convert(invalidJson);

        result.Should().Be(invalidJson);
    }

    #endregion
}
