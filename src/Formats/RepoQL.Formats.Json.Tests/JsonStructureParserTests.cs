using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using RepoQL.Formats.Json;

namespace RepoQL.Formats.Json.Tests;

public sealed class JsonStructureParserTests
{
    [Test]
    [DisplayName("Parses flat objects with expected shape, paths, and line numbers")]
    public void Parse_FlatObject_ProducesExpectedKeys()
    {
        var parser = new JsonStructureParser();
        const string json = """
        {
          "name": "repoql",
          "version": 1,
          "enabled": true
        }
        """;

        var result = parser.Parse(json);

        result.Shape.Should().Be(JsonShape.FlatObject);
        result.TotalKeyCount.Should().Be(3);
        result.Keys.Should().HaveCount(3);

        var name = FindKey(result, "/name");
        name.ValueKind.Should().Be(JsonValueKind.String);
        name.Depth.Should().Be(0);
        name.StartLine.Should().Be(2);
        name.EndLine.Should().Be(2);

        var version = FindKey(result, "/version");
        version.ValueKind.Should().Be(JsonValueKind.Number);
        version.StartLine.Should().Be(3);

        var enabled = FindKey(result, "/enabled");
        enabled.ValueKind.Should().Be(JsonValueKind.True);
        enabled.StartLine.Should().Be(4);
    }

    [Test]
    [DisplayName("Parses nested objects with expected depth and pointer paths")]
    public void Parse_NestedObject_ProducesExpectedDepthAndPaths()
    {
        var parser = new JsonStructureParser();
        const string json = """
        {
          "database": {
            "host": "localhost",
            "port": 5432
          }
        }
        """;

        var result = parser.Parse(json);

        result.Shape.Should().Be(JsonShape.NestedObject);

        var database = FindKey(result, "/database");
        database.Depth.Should().Be(0);
        database.ValueKind.Should().Be(JsonValueKind.Object);

        var host = FindKey(result, "/database/host");
        host.Depth.Should().Be(1);
        host.ValueKind.Should().Be(JsonValueKind.String);

        var port = FindKey(result, "/database/port");
        port.Depth.Should().Be(1);
        port.ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Test]
    [DisplayName("Detects array shape and parses indexed element paths")]
    public void Parse_RootArray_ProducesArrayShapeAndLength()
    {
        var parser = new JsonStructureParser();
        const string json = """
        [
          { "name": "alpha" },
          { "name": "beta" }
        ]
        """;

        var result = parser.Parse(json);

        result.Shape.Should().Be(JsonShape.Array);
        result.ArrayLength.Should().Be(2);
        result.TotalKeyCount.Should().Be(2);

        var first = FindKey(result, "/0/name");
        first.Depth.Should().Be(1);

        var second = FindKey(result, "/1/name");
        second.Depth.Should().Be(1);
    }

    [Test]
    [DisplayName("Parses nested arrays with paths like /data/0/name")]
    public void Parse_NestedArray_ProducesObjectArrayPaths()
    {
        var parser = new JsonStructureParser();
        const string json = """
        {
          "data": [
            { "name": "alpha" },
            { "name": "beta" }
          ]
        }
        """;

        var result = parser.Parse(json);

        result.Shape.Should().Be(JsonShape.NestedObject);

        var data = FindKey(result, "/data");
        data.ValueKind.Should().Be(JsonValueKind.Array);
        data.ArrayLength.Should().Be(2);

        var nestedName = FindKey(result, "/data/0/name");
        nestedName.Depth.Should().Be(2);
    }

    [Test]
    [DisplayName("Returns Empty shape for empty or whitespace input")]
    public void Parse_WhitespaceOnly_ReturnsEmptyShape()
    {
        var parser = new JsonStructureParser();

        var result = parser.Parse(" \r\n  \t");

        result.Shape.Should().Be(JsonShape.Empty);
        result.Keys.Should().BeEmpty();
        result.TotalKeyCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Throws ArgumentNullException for null string input")]
    public void Parse_NullString_ThrowsArgumentNullException()
    {
        var parser = new JsonStructureParser();

        Action act = () => parser.Parse((string)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    [DisplayName("Throws JsonException on malformed JSON")]
    public void Parse_MalformedJson_ThrowsJsonException()
    {
        var parser = new JsonStructureParser();

        Action act = () => parser.Parse("{\"name\": \"repoql\"");

        act.Should().Throw<JsonException>();
    }

    [Test]
    [DisplayName("Root array sampling stops after MaxSampleRecords")]
    public void Parse_RootArraySampling_StopsAtConfiguredSampleCount()
    {
        var parser = new JsonStructureParser();
        const string json = """
        [
          {"id": 1},
          {"id": 2},
          {"id": 3},
          {"id": 4},
          {"id": 5}
        ]
        """;

        var result = parser.Parse(json, new JsonParseOptions
        {
            MaxSampleRecords = 2
        });

        result.Shape.Should().Be(JsonShape.Array);
        result.TotalKeyCount.Should().Be(2);
        result.Keys.Should().Contain(k => k.Path == "/0/id");
        result.Keys.Should().Contain(k => k.Path == "/1/id");
        result.Keys.Should().NotContain(k => k.Path == "/2/id");
        result.ArrayLength.Should().NotBeNull();
        result.ArrayLength!.Value.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    [DisplayName("Parses JSONL line-by-line with array shape and sampling")]
    public void Parse_Jsonl_ParsesSampledLinesAsArrayElements()
    {
        var parser = new JsonStructureParser();
        const string jsonl = """
        {"id":1,"name":"a"}
        {"id":2,"name":"b"}
        {"id":3,"name":"c"}
        """;

        var result = parser.Parse(jsonl, new JsonParseOptions
        {
            IsJsonl = true,
            MaxSampleRecords = 2
        });

        result.Shape.Should().Be(JsonShape.Array);
        result.TotalKeyCount.Should().Be(4);
        result.Keys.Should().Contain(k => k.Path == "/0/id");
        result.Keys.Should().Contain(k => k.Path == "/0/name");
        result.Keys.Should().Contain(k => k.Path == "/1/id");
        result.Keys.Should().Contain(k => k.Path == "/1/name");
        result.Keys.Should().NotContain(k => k.Path == "/2/id");
        result.ArrayLength.Should().NotBeNull();
        result.ArrayLength!.Value.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    [DisplayName("JSONL mode skips malformed sampled lines")]
    public void Parse_Jsonl_SkipsMalformedSampledLines()
    {
        var parser = new JsonStructureParser();
        const string jsonl = """
        {"id":1}
        not-json
        {"id":3}
        """;

        var result = parser.Parse(jsonl, new JsonParseOptions
        {
            IsJsonl = true,
            MaxSampleRecords = 3
        });

        result.Shape.Should().Be(JsonShape.Array);
        result.Keys.Should().Contain(k => k.Path == "/0/id");
        result.Keys.Should().Contain(k => k.Path == "/2/id");
        result.TotalKeyCount.Should().Be(2);
    }

    [Test]
    [DisplayName("Applies node selection heuristic for depth and MaxNodes cap")]
    public void Parse_NodeSelection_UsesDepthRuleAndCap()
    {
        var parser = new JsonStructureParser();
        const string json = """
        {
          "topScalar": 1,
          "topObject": {
            "nestedScalar": 2,
            "nestedArray": [1, 2, 3],
            "nestedObject": { "deepScalar": 3 }
          },
          "anotherRoot": {}
        }
        """;

        var result = parser.Parse(json, new JsonParseOptions
        {
            MaxNodeDepth = 1,
            MaxNodes = 2
        });

        FindKey(result, "/topScalar").IsNodeEligible.Should().BeTrue();
        FindKey(result, "/topObject").IsNodeEligible.Should().BeTrue();

        FindKey(result, "/topObject/nestedScalar").IsNodeEligible.Should().BeFalse();
        FindKey(result, "/topObject/nestedArray").IsNodeEligible.Should().BeFalse();
        FindKey(result, "/topObject/nestedObject").IsNodeEligible.Should().BeFalse();
        FindKey(result, "/topObject/nestedObject/deepScalar").IsNodeEligible.Should().BeFalse();
        FindKey(result, "/anotherRoot").IsNodeEligible.Should().BeFalse();
    }

    [Test]
    [DisplayName("Resolves line numbers correctly with multi-byte UTF-8 characters")]
    public void Parse_MultiByteUtf8_ResolvesLineNumbers()
    {
        var parser = new JsonStructureParser();
        const string json = """
        {
          "title": "こんにちは",
          "emoji": "😀",
          "next": {
            "value": 1
          }
        }
        """;

        var result = parser.Parse(json);

        var next = FindKey(result, "/next");
        next.StartLine.Should().Be(4);
        next.EndLine.Should().Be(6);

        var nested = FindKey(result, "/next/value");
        nested.StartLine.Should().Be(5);
        nested.EndLine.Should().Be(5);
    }

    [Test]
    [DisplayName("Escapes JSON Pointer segments for '~' and '/' characters")]
    public void Parse_JsonPointerEscaping_UsesRfc6901Escapes()
    {
        var parser = new JsonStructureParser();
        const string json = """
        {
          "a/b": {
            "~key": 1
          }
        }
        """;

        var result = parser.Parse(json);

        result.Keys.Should().Contain(k => k.Path == "/a~1b");
        result.Keys.Should().Contain(k => k.Path == "/a~1b/~0key");
    }

    [Test]
    [DisplayName("Truncates scalar values to 100 characters")]
    public void Parse_ScalarValue_TruncatesAtOneHundredCharacters()
    {
        var parser = new JsonStructureParser();
        var longValue = new string('x', 140);
        var json = $"{{\"message\":\"{longValue}\"}}";

        var result = parser.Parse(json);
        var message = FindKey(result, "/message");

        message.ScalarValue.Should().NotBeNull();
        message.ScalarValue!.Length.Should().Be(100);
        message.ScalarValue.Should().Be(new string('x', 100));
    }

    [Test]
    [DisplayName("Estimates subtree tokens using byte-span divided by four")]
    public void Parse_EstimatedTokens_UsesByteSpanHeuristic()
    {
        var parser = new JsonStructureParser();
        const string json = "{" + "\"obj\":{\"a\":1},\"name\":\"abc\"" + "}";

        var result = parser.Parse(json);

        var obj = FindKey(result, "/obj");
        var expectedObjectTokens = Encoding.UTF8.GetByteCount("{\"a\":1}") / 4;
        obj.EstimatedTokens.Should().Be(expectedObjectTokens);

        var name = FindKey(result, "/name");
        var expectedScalarTokens = Encoding.UTF8.GetByteCount("abc") / 4;
        name.EstimatedTokens.Should().Be(expectedScalarTokens);
    }

    private static JsonKeyInfo FindKey(JsonParseResult result, string path)
        => result.Keys.Single(k => k.Path == path);
}
