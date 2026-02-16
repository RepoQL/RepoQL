using AwesomeAssertions;
using RepoQL.Data.DuckDB.UdfImplementations;

namespace RepoQL.Data.DuckDB.Tests;

public class StructuredDataExtractorTests
{
    #region Extract - Pure JSON input

    [Test]
    public async Task Extract_WithJsonArray_ReturnsAsIs()
    {
        var input = """[{"name":"test","value":123}]""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_WithJsonObject_ReturnsAsIs()
    {
        var input = """{"name":"test","value":123}""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_WithWhitespaceBeforeJson_ReturnsJson()
    {
        var input = "   [{\"name\":\"test\"}]";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("[{\"name\":\"test\"}]");
    }

    #endregion

    #region Extract - JSONL (JSON Lines)

    [Test]
    public async Task Extract_Jsonl_ParsesMultipleObjects()
    {
        var input = """
            {"id": 1, "name": "Alice"}
            {"id": 2, "name": "Bob"}
            {"id": 3, "name": "Charlie"}
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().StartWith("[{");
        result.Should().EndWith("}]");
        result.Should().Contain("\"id\": 1");
        result.Should().Contain("\"id\": 2");
        result.Should().Contain("\"id\": 3");
    }

    [Test]
    public async Task Extract_Jsonl_SingleLineNotJsonl()
    {
        var input = """{"id": 1, "name": "Alice"}""";

        var result = StructuredDataExtractor.Extract(input);

        // Single line should be treated as regular JSON, not JSONL
        result.Should().Be(input);
    }

    #endregion

    #region Extract - CSV

    [Test]
    public async Task Extract_Csv_ParsesWithHeaders()
    {
        var input = """
            id,name,status
            1,Alice,active
            2,Bob,inactive
            3,Charlie,active
            """;

        var result = StructuredDataExtractor.Extract(input);

        // Numeric IDs are parsed as numbers, not strings
        result.Should().Contain("\"id\":1");
        result.Should().Contain("\"name\":\"Alice\"");
        result.Should().Contain("\"status\":\"active\"");
    }

    [Test]
    public async Task Extract_Csv_HandlesQuotedFields()
    {
        var input = """
            id,name,description
            1,"Alice, Jr.","A ""special"" person"
            2,Bob,Normal person
            3,Charlie,Another one
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("Alice, Jr.");
        // In JSON output, quotes are escaped as \u0022 or \"
        result.Should().Contain("special");
        result.Should().Contain("person");
    }

    [Test]
    public async Task Extract_SingleColumn_NotDetectedAsCsv()
    {
        var input = """
            items
            apple
            banana
            cherry
            """;

        var result = StructuredDataExtractor.Extract(input);

        // Single column is not tabular - should wrap as text
        result.Should().Contain("\"text\":");
    }

    [Test]
    public async Task Extract_CommaSeparatedProse_NotDetectedAsCsv()
    {
        var input = "Error at line 42, column 15, file main.cs";

        var result = StructuredDataExtractor.Extract(input);

        // Single line prose should wrap as text
        result.Should().Contain("\"text\":");
    }

    #endregion

    #region Extract - TSV

    [Test]
    public async Task Extract_Tsv_ParsesWithHeaders()
    {
        var input = "id\tname\tstatus\n1\tAlice\tactive\n2\tBob\tinactive\n3\tCharlie\tactive";

        var result = StructuredDataExtractor.Extract(input);

        // Numeric IDs are parsed as numbers, not strings
        result.Should().Contain("\"id\":1");
        result.Should().Contain("\"name\":\"Alice\"");
        result.Should().Contain("\"status\":\"active\"");
    }

    [Test]
    public async Task Extract_TabFormattedOutput_NotDetectedAsTsv()
    {
        // Only 2 rows - below threshold
        var input = "Error\tat line 42\nStack\tSystem.Exception";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"text\":");
    }

    #endregion

    #region Extract - YAML

    [Test]
    public async Task Extract_YamlDocument_ConvertsToJson()
    {
        var input = """
            ---
            name: Test
            value: 123
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"name\"");
        result.Should().Contain("Test");
    }

    [Test]
    public async Task Extract_YamlKeyValue_ConvertsToJson()
    {
        var input = """
            server: localhost
            port: 8080
            enabled: true
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"server\"");
        result.Should().Contain("localhost");
    }

    [Test]
    public async Task Extract_ProseWithColons_NotDetectedAsYaml()
    {
        var input = "The problem: authentication is failing.";

        var result = StructuredDataExtractor.Extract(input);

        // Single line with colon is prose, not YAML
        result.Should().Contain("\"text\":");
    }

    [Test]
    public async Task Extract_ErrorMessage_NotDetectedAsYaml()
    {
        var input = "Error: connection timeout\nRetrying in 5 seconds...";

        var result = StructuredDataExtractor.Extract(input);

        // "Retrying..." doesn't match key: value pattern
        result.Should().Contain("\"text\":");
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

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("""[{"resource_name":"host","state":"Running"},{"resource_name":"web","state":"Running"}]""");
    }

    [Test]
    public async Task Extract_WithMarkdownAndJsonObject_ExtractsJson()
    {
        var input = """
            Some preamble text.

            {"id":1,"name":"test"}
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("""{"id":1,"name":"test"}""");
    }

    [Test]
    public async Task Extract_WithNestedJson_ExtractsOuterStructure()
    {
        var input = """
            Explanation text here.

            [{"outer":{"inner":"value"},"list":[1,2,3]}]
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("""[{"outer":{"inner":"value"},"list":[1,2,3]}]""");
    }

    [Test]
    public async Task Extract_WithJsonContainingEscapedQuotes_ExtractsCorrectly()
    {
        var input = """
            Markdown text.

            [{"message":"He said \"hello\""}]
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("""[{"message":"He said \"hello\""}]""");
    }

    [Test]
    public async Task Extract_WithJsonContainingBracketsInStrings_ExtractsCorrectly()
    {
        var input = """
            Description.

            [{"pattern":"[a-z]+","example":"{foo}"}]
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("""[{"pattern":"[a-z]+","example":"{foo}"}]""");
    }

    #endregion

    #region Extract - Error cases

    [Test]
    public async Task Extract_WithNullInput_ReturnsNull()
    {
        var result = StructuredDataExtractor.Extract(null);

        result.Should().Be("null");
    }

    [Test]
    public async Task Extract_WithEmptyInput_ReturnsNull()
    {
        var result = StructuredDataExtractor.Extract("");

        result.Should().Be("null");
    }

    [Test]
    public async Task Extract_WithNoJson_ReturnsWrappedText()
    {
        var input = "This is just plain text with no JSON.";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"text\":");
        result.Should().Contain("This is just plain text");
    }

    [Test]
    public async Task Extract_WithInvalidJson_ReturnsWrappedText()
    {
        var input = "[{invalid json here";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"text\":");
    }

    [Test]
    public async Task Extract_WithPlainMessage_WrapsAsText()
    {
        var input = "An error occurred invoking 'list_resources'.";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"text\":");
        result.Should().Contain("An error occurred");
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

        var result = StructuredDataExtractor.Extract(input);

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

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be("""[{"severity":"Error","message":"Connection failed"},{"severity":"Info","message":"Started"}]""");
    }

    #endregion

    #region ExtractByBracketMatching

    [Test]
    public async Task ExtractByBracketMatching_WithSimpleArray_ExtractsIt()
    {
        var input = "prefix [1,2,3] suffix";

        var result = StructuredDataExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().Be("[1,2,3]");
    }

    [Test]
    public async Task ExtractByBracketMatching_WithNestedBrackets_FindsOuterMatch()
    {
        var input = "text [[1,2],[3,4]] more";

        var result = StructuredDataExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().Be("[[1,2],[3,4]]");
    }

    [Test]
    public async Task ExtractByBracketMatching_WithNoBrackets_ReturnsNull()
    {
        var input = "no brackets here";

        var result = StructuredDataExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().BeNull();
    }

    [Test]
    public async Task ExtractByBracketMatching_WithUnmatchedBrackets_ReturnsNull()
    {
        var input = "unmatched [bracket";

        var result = StructuredDataExtractor.ExtractByBracketMatching(input, '[', ']');

        result.Should().BeNull();
    }

    [Test]
    public async Task ExtractByBracketMatching_SkipsInvalidJsonAndTriesNext()
    {
        // First bracket pair is not valid JSON, second is
        var input = "text [not json] more [1,2,3] end";

        var result = StructuredDataExtractor.ExtractByBracketMatching(input, '[', ']');

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
        var result = StructuredDataExtractor.IsValidJson(json);

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
        var result = StructuredDataExtractor.IsValidJson(json);

        result.Should().BeFalse();
    }

    #endregion

    #region WrapAsText

    [Test]
    public async Task WrapAsText_EscapesQuotes()
    {
        var input = """He said "hello".""";

        var result = StructuredDataExtractor.WrapAsText(input);

        result.Should().Contain("\\\"hello\\\"");
    }

    [Test]
    public async Task WrapAsText_EscapesNewlines()
    {
        var input = "line1\nline2\rline3";

        var result = StructuredDataExtractor.WrapAsText(input);

        result.Should().Contain("\\n");
        result.Should().Contain("\\r");
    }

    [Test]
    public async Task WrapAsText_EscapesBackslashes()
    {
        var input = """path\to\file""";

        var result = StructuredDataExtractor.WrapAsText(input);

        result.Should().Contain("\\\\");
    }

    #endregion

    #region Extract - JSON Unwrapping

    [Test]
    public async Task Extract_WithSimpleEnvelope_UnwrapsToArray()
    {
        var input = """{"data": [{"id": 1, "name": "Alice"}, {"id": 2, "name": "Bob"}]}""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().StartWith("[");
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
        result.Should().NotContain("\"data\"");
    }

    [Test]
    public async Task Extract_WithNerdGraphEnvelope_UnwrapsToDeepArray()
    {
        var input = """
            {
              "data": {
                "actor": {
                  "entitySearch": {
                    "results": {
                      "nextCursor": "abc123",
                      "entities": [
                        {"name": "web-app", "guid": "abc", "accountId": 123},
                        {"name": "api-svc", "guid": "def", "accountId": 456}
                      ]
                    }
                  }
                }
              }
            }
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().StartWith("[");
        result.Should().Contain("\"web-app\"");
        result.Should().Contain("\"api-svc\"");
        result.Should().NotContain("\"data\"");
        result.Should().NotContain("\"actor\"");
        result.Should().NotContain("\"nextCursor\"");
    }

    [Test]
    public async Task Extract_WithMultipleArrays_PicksLargest()
    {
        var input = """
            {
              "results": [{"id": 1}, {"id": 2}, {"id": 3}],
              "errors": [{"code": 500}]
            }
            """;

        var result = StructuredDataExtractor.Extract(input);

        result.Should().StartWith("[");
        result.Should().Contain("\"id\"");
        result.Should().NotContain("\"code\"");
    }

    [Test]
    public async Task Extract_WithSameSizeArrays_PicksDeeper()
    {
        var input = """{"shallow": [{"x": 1}], "deep": {"nested": [{"y": 1}]}}""";

        var result = StructuredDataExtractor.Extract(input);

        // Both size 1, deeper wins
        result.Should().Contain("\"y\"");
        result.Should().NotContain("\"x\"");
    }

    [Test]
    public async Task Extract_WithArrayOfPrimitives_NoUnwrapping()
    {
        var input = """{"tags": ["red", "blue", "green"]}""";

        var result = StructuredDataExtractor.Extract(input);

        // Array of strings, not objects — no unwrapping
        result.Should().Contain("\"tags\"");
    }

    [Test]
    public async Task Extract_DoesNotRecurseIntoArrayElements()
    {
        var input = """
            {
              "users": [
                {"id": 1, "tags": [{"name": "admin"}, {"name": "editor"}, {"name": "viewer"}]},
                {"id": 2, "tags": [{"name": "user"}]}
              ]
            }
            """;

        var result = StructuredDataExtractor.Extract(input);

        // Should pick "users" (2 items), NOT "tags" inside array elements
        // Even though tags[0] has 3 items, we don't recurse into array elements
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"tags\"");
    }

    [Test]
    public async Task Extract_WithUnwrapFalse_PreservesEnvelope()
    {
        var input = """{"data": [{"id": 1}, {"id": 2}]}""";

        var result = StructuredDataExtractor.Extract(input, unwrap: false);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_AlreadyArray_NoChange()
    {
        var input = """[{"id": 1}, {"id": 2}]""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_PlainObjectNoArrays_NoChange()
    {
        var input = """{"status": "ok", "count": 42}""";

        var result = StructuredDataExtractor.Extract(input);

        // No array of objects found — returns as single-row table
        result.Should().Contain("\"status\"");
        result.Should().Contain("\"count\"");
    }

    [Test]
    public async Task Extract_EmptyObject_NoChange()
    {
        var input = "{}";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_EmptyArrayField_NoUnwrapping()
    {
        var input = """{"results": []}""";

        var result = StructuredDataExtractor.Extract(input);

        // Empty array has no objects — no unwrapping
        result.Should().Contain("\"results\"");
    }

    #endregion

    #region TryParseStructuredText

    [Test]
    public async Task TryParseStructuredText_ParsesContext7Format()
    {
        var input = """
Header text
----------
- Title: DuckDB
- Library ID: /duckdb/duckdb
- Description: A database
----------
- Title: SQLite
- Library ID: /sqlite/sqlite
- Description: Another database
""";

        var result = StructuredDataExtractor.TryParseStructuredText(input);

        result.Should().NotBeNull();
        result.Should().Contain("\"title\":\"DuckDB\"");
        result.Should().Contain("\"library_id\":\"/duckdb/duckdb\"");
        result.Should().Contain("\"title\":\"SQLite\"");
    }

    [Test]
    public async Task TryParseStructuredText_ReturnsNullForPlainText()
    {
        var input = "Just some plain text without structure";

        var result = StructuredDataExtractor.TryParseStructuredText(input);

        result.Should().BeNull();
    }

    #endregion

    #region Extract - Transport-escaped JSON recovery

    [Test]
    public async Task Extract_WithDoubleEscapedJsonArray_UnescapesAndReturnsJson()
    {
        // MCP JSON-RPC transport double-escapes: {"id":1} becomes {\"id\":1}
        var input = """[{\"service\":\"Identity\",\"team\":\"Foundation\"}]""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"service\"");
        result.Should().Contain("Identity");
        StructuredDataExtractor.IsValidJson(result).Should().BeTrue();
    }

    [Test]
    public async Task Extract_WithDoubleEscapedJsonObject_UnescapesAndReturnsJson()
    {
        var input = """{\"name\":\"test\",\"value\":123}""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Contain("\"name\"");
        result.Should().Contain("test");
        StructuredDataExtractor.IsValidJson(result).Should().BeTrue();
    }

    [Test]
    public async Task Extract_WithValidJson_DoesNotAttemptUnescape()
    {
        // Already-valid JSON containing literal \" inside string values — must not be modified
        var input = """[{"message":"He said \"hello\""}]""";

        var result = StructuredDataExtractor.Extract(input);

        result.Should().Be(input);
    }

    [Test]
    public async Task Extract_WithPartiallyEscapedJson_FallsThrough()
    {
        // Only some quotes escaped — unescaping won't produce valid JSON, should fall through
        var input = """[{\"service":"Identity"}]""";

        var result = StructuredDataExtractor.Extract(input);

        // Should not crash; may fall through to CSV or text wrapping
        result.Should().NotBeNull();
    }

    [Test]
    public async Task TryUnescapeJsonTransportLayer_WithCleanJson_ReturnsNull()
    {
        // No escaped quotes — nothing to unescape
        var result = StructuredDataExtractor.TryUnescapeJsonTransportLayer("""[{"id":1}]""");

        result.Should().BeNull();
    }

    [Test]
    public async Task TryUnescapeJsonTransportLayer_WithDoubleEscaped_ReturnsValidJson()
    {
        var result = StructuredDataExtractor.TryUnescapeJsonTransportLayer("""[{\"id\":1}]""");

        result.Should().NotBeNull();
        result.Should().Be("""[{"id":1}]""");
    }

    [Test]
    public async Task TryUnescapeJsonTransportLayer_WithGarbage_ReturnsNull()
    {
        // Contains \" but unescaping doesn't produce valid JSON
        var result = StructuredDataExtractor.TryUnescapeJsonTransportLayer("""not json \" at all""");

        result.Should().BeNull();
    }

    #endregion
}
