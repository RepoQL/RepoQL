using AwesomeAssertions;

namespace RepoQL.Mcp.Client.Tests;

public class JsonResponseExtractorTests
{
    #region Extract - Pure JSON input

    [Test]
    public async Task Extract_WithJsonArray_ReturnsAsIs()
    {
        var input = """[{"name":"test","value":123}]""";

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_WithJsonObject_ReturnsAsIs()
    {
        var input = """{"name":"test","value":123}""";

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_WithWhitespaceBeforeJson_ReturnsJson()
    {
        var input = "   [{\"name\":\"test\"}]";

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("[{\"name\":\"test\"}]");
    }

    #endregion

    #region Extract - Markdown with embedded JSON

    [Test]
    public async Task Extract_WithMarkdownAndJsonArray_ExtractsJson()
    {
        var input = """
            Here is some explanation about the data.

            # DATA

            [{"resource_name":"host","state":"Running"},{"resource_name":"web","state":"Running"}]
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""[{"resource_name":"host","state":"Running"},{"resource_name":"web","state":"Running"}]""");
    }

    [Test]
    public async Task Extract_WithMarkdownAndJsonObject_ExtractsJson()
    {
        var input = """
            Some preamble text.

            {"id":1,"name":"test"}
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""{"id":1,"name":"test"}""");
    }

    [Test]
    public async Task Extract_WithNestedJson_ExtractsOuterStructure()
    {
        var input = """
            Explanation text here.

            [{"outer":{"inner":"value"},"list":[1,2,3]}]
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""[{"outer":{"inner":"value"},"list":[1,2,3]}]""");
    }

    [Test]
    public async Task Extract_WithJsonContainingEscapedQuotes_ExtractsCorrectly()
    {
        var input = """
            Markdown text.

            [{"message":"He said \"hello\""}]
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""[{"message":"He said \"hello\""}]""");
    }

    [Test]
    public async Task Extract_WithJsonContainingBracketsInStrings_ExtractsCorrectly()
    {
        var input = """
            Description.

            [{"pattern":"[a-z]+","example":"{foo}"}]
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""[{"pattern":"[a-z]+","example":"{foo}"}]""");
    }

    #endregion

    #region Extract - Error cases

    [Test]
    public async Task Extract_WithNullInput_ReturnsNull()
    {
        var result = JsonResponseExtractor.Extract(null);

        result.Should().Be("null");
    }

    [Test]
    public async Task Extract_WithEmptyInput_ReturnsNull()
    {
        var result = JsonResponseExtractor.Extract("");

        result.Should().Be("null");
    }

    [Test]
    public async Task Extract_WithNoJson_ReturnsWrappedError()
    {
        var input = "This is just plain text with no JSON.";

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Contain("\"error\":");
        result.Should().Contain("This is just plain text");
    }

    [Test]
    public async Task Extract_WithInvalidJson_ReturnsWrappedError()
    {
        var input = "[{invalid json here";

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Contain("\"error\":");
    }

    [Test]
    public async Task Extract_WithErrorMessage_WrapsAsError()
    {
        var input = "An error occurred invoking 'list_resources'.";

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""{"error": "An error occurred invoking 'list_resources'."}""");
    }

    #endregion

    #region Extract - Real-world aspire-dashboard format

    [Test]
    public async Task Extract_AspireDashboardResourcesFormat_ExtractsJson()
    {
        var input = """
            resource_name is the identifier of resources. Use the dashboard_link when displaying resource_name.
            environment_variables is a list of environment variables configured for the resource.

            # RESOURCE DATA

            [{"resource_name":"host","type":"Project","state":"Running"}]
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""[{"resource_name":"host","type":"Project","state":"Running"}]""");
    }

    [Test]
    public async Task Extract_AspireDashboardLogsFormat_ExtractsJson()
    {
        var input = """
            structured_logs includes logs from all resources.

            # STRUCTURED LOGS

            [{"severity":"Error","message":"Connection failed"},{"severity":"Info","message":"Started"}]
            """;

        var result = JsonResponseExtractor.Extract(input);

        result.Should().Be("""[{"severity":"Error","message":"Connection failed"},{"severity":"Info","message":"Started"}]""");
    }

    #endregion

    #region ExtractByBracketMatching

    [Test]
    public async Task ExtractByBracketMatching_WithSimpleArray_ExtractsIt()
    {
        var input = "prefix [1,2,3] suffix";

        var result = JsonResponseExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().Be("[1,2,3]");
    }

    [Test]
    public async Task ExtractByBracketMatching_WithNestedBrackets_FindsOuterMatch()
    {
        var input = "text [[1,2],[3,4]] more";

        var result = JsonResponseExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().Be("[[1,2],[3,4]]");
    }

    [Test]
    public async Task ExtractByBracketMatching_WithNoBrackets_ReturnsNull()
    {
        var input = "no brackets here";

        var result = JsonResponseExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().BeNull();
    }

    [Test]
    public async Task ExtractByBracketMatching_WithUnmatchedBrackets_ReturnsNull()
    {
        var input = "unmatched [bracket";

        var result = JsonResponseExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().BeNull();
    }

    [Test]
    public async Task ExtractByBracketMatching_SkipsInvalidJsonAndTriesNext()
    {
        // First bracket pair is not valid JSON, second is
        var input = "text [not json] more [1,2,3] end";

        var result = JsonResponseExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().Be("[1,2,3]");
    }

    #endregion

    #region IsValidJson

    [Test]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("[1,2,3]")]
    [Arguments("""{"key":"value"}""")]
    [Arguments("""[{"nested":{"deep":true}}]""")]
    public async Task IsValidJson_WithValidJson_ReturnsTrue(string json)
    {
        var result = JsonResponseExtractor.IsValidJson(json);

        result.Should().BeTrue();
    }

    [Test]
    [Arguments("[")]
    [Arguments("{")]
    [Arguments("[1,2,")]
    [Arguments("{invalid}")]
    [Arguments("not json")]
    public async Task IsValidJson_WithInvalidJson_ReturnsFalse(string json)
    {
        var result = JsonResponseExtractor.IsValidJson(json);

        result.Should().BeFalse();
    }

    #endregion

    #region WrapAsError

    [Test]
    public async Task WrapAsError_EscapesQuotes()
    {
        var input = """He said "hello".""";

        var result = JsonResponseExtractor.WrapAsError(input);

        result.Should().Be("""{"error": "He said \"hello\"."}""");
    }

    [Test]
    public async Task WrapAsError_EscapesNewlines()
    {
        var input = "line1\nline2\rline3";

        var result = JsonResponseExtractor.WrapAsError(input);

        result.Should().Be("""{"error": "line1\nline2\rline3"}""");
    }

    [Test]
    public async Task WrapAsError_EscapesBackslashes()
    {
        var input = """path\to\file""";

        var result = JsonResponseExtractor.WrapAsError(input);

        result.Should().Be("""{"error": "path\\to\\file"}""");
    }

    #endregion
}
