using AwesomeAssertions;

namespace RepoQL.Formats.Json.Tests;

public sealed class Json5NormalizerTests
{
    [Test]
    [DisplayName("Valid JSON passes through unchanged")]
    public void Normalize_ValidJson_PassesThrough()
    {
        const string json = """{"name":"repoql","count":42,"enabled":true}""";

        var result = Json5Normalizer.Normalize(json);

        result.Should().Be(json);
    }

    [Test]
    [DisplayName("Trailing comma in object is removed")]
    public void Normalize_TrailingCommaInObject()
    {
        const string input = """{"a":1,"b":2,}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"a":1,"b":2}""");
    }

    [Test]
    [DisplayName("Trailing comma in array is removed")]
    public void Normalize_TrailingCommaInArray()
    {
        const string input = """[1,2,3,]""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("[1,2,3]");
    }

    [Test]
    [DisplayName("Trailing comma with whitespace before closing brace")]
    public void Normalize_TrailingCommaWithWhitespace()
    {
        var input = "{\n  \"a\": 1,\n}";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("{\n  \"a\": 1\n}");
    }

    [Test]
    [DisplayName("Trailing comma followed by comment then closing brace")]
    public void Normalize_TrailingCommaWithComment()
    {
        var input = "{\n  \"a\": 1, // last item\n}";

        var result = Json5Normalizer.Normalize(input);

        // The comma is removed; the comment becomes whitespace; the inline space remains
        result.Should().Be("{\n  \"a\": 1 \n}");
    }

    [Test]
    [DisplayName("Non-trailing comma is preserved")]
    public void Normalize_NonTrailingCommaPreserved()
    {
        const string input = """{"a":1,"b":2}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be(input);
    }

    [Test]
    [DisplayName("Unquoted key is wrapped in double quotes")]
    public void Normalize_UnquotedKey()
    {
        const string input = """{name:"repoql"}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"name":"repoql"}""");
    }

    [Test]
    [DisplayName("Unquoted key with digits, underscores, and dollar signs")]
    public void Normalize_UnquotedKeyWithSpecialChars()
    {
        const string input = """{$key_1:"value"}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"$key_1":"value"}""");
    }

    [Test]
    [DisplayName("Unquoted key with whitespace before colon")]
    public void Normalize_UnquotedKeyWithSpaceBeforeColon()
    {
        const string input = """{name : "value"}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"name" : "value"}""");
    }

    [Test]
    [DisplayName("Multiple unquoted keys")]
    public void Normalize_MultipleUnquotedKeys()
    {
        const string input = """{name:"a",count:42}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"name":"a","count":42}""");
    }

    [Test]
    [DisplayName("true, false, null identifiers are preserved as-is")]
    public void Normalize_JsonLiteralsPreserved()
    {
        const string input = """{"a":true,"b":false,"c":null}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be(input);
    }

    [Test]
    [DisplayName("Single-quoted string is converted to double-quoted")]
    public void Normalize_SingleQuotedString()
    {
        const string input = """{'key':'value'}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"key":"value"}""");
    }

    [Test]
    [DisplayName("Single-quoted string with internal double quote escapes it")]
    public void Normalize_SingleQuotedStringWithDoubleQuote()
    {
        var input = """{'key':'say "hello"'}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"key":"say \"hello\""}""");
    }

    [Test]
    [DisplayName("Single-quoted string with escaped apostrophe")]
    public void Normalize_SingleQuotedStringWithEscapedApostrophe()
    {
        var input = """{'key':'it\'s fine'}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"key":"it's fine"}""");
    }

    [Test]
    [DisplayName("Hex number is converted to decimal")]
    public void Normalize_HexNumber()
    {
        const string input = """{"value":0xFF}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":255}""");
    }

    [Test]
    [DisplayName("Hex number uppercase")]
    public void Normalize_HexNumberUpperCase()
    {
        const string input = """{"value":0X1A}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":26}""");
    }

    [Test]
    [DisplayName("Infinity is replaced with null")]
    public void Normalize_Infinity()
    {
        const string input = """{"value":Infinity}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":null}""");
    }

    [Test]
    [DisplayName("Negative Infinity is replaced with null")]
    public void Normalize_NegativeInfinity()
    {
        const string input = """{"value":-Infinity}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":null}""");
    }

    [Test]
    [DisplayName("Positive Infinity is replaced with null")]
    public void Normalize_PositiveInfinity()
    {
        const string input = """{"value":+Infinity}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":null}""");
    }

    [Test]
    [DisplayName("NaN is replaced with null")]
    public void Normalize_NaN()
    {
        const string input = """{"value":NaN}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":null}""");
    }

    [Test]
    [DisplayName("Line comment is removed and newline preserved")]
    public void Normalize_LineComment()
    {
        var input = "{\n  // comment\n  \"a\": 1\n}";

        var result = Json5Normalizer.Normalize(input);

        // Leading spaces before // are preserved, comment content stripped
        result.Should().Be("{\n  \n  \"a\": 1\n}");
    }

    [Test]
    [DisplayName("Block comment is removed and newlines preserved")]
    public void Normalize_BlockComment()
    {
        var input = "{\n  /* block\n     comment */\n  \"a\": 1\n}";

        var result = Json5Normalizer.Normalize(input);

        // Leading spaces before /* preserved, newlines within block preserved
        result.Should().Be("{\n  \n\n  \"a\": 1\n}");
    }

    [Test]
    [DisplayName("Comment syntax inside strings is preserved")]
    public void Normalize_CommentInsideString()
    {
        const string input = """{"url":"https://example.com","comment":"/* not a comment */"}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be(input);
    }

    [Test]
    [DisplayName("Multi-line double-quoted string with backslash continuation")]
    public void Normalize_MultiLineDoubleString()
    {
        var input = "{\"key\":\"line1\\\nline2\"}";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("{\"key\":\"line1line2\"}");
    }

    [Test]
    [DisplayName("Multi-line single-quoted string with backslash continuation")]
    public void Normalize_MultiLineSingleString()
    {
        var input = "{'key':'line1\\\nline2'}";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("{\"key\":\"line1line2\"}");
    }

    [Test]
    [DisplayName("Leading + on number is stripped")]
    public void Normalize_LeadingPlusOnNumber()
    {
        const string input = """{"value":+42}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"value":42}""");
    }

    [Test]
    [DisplayName("Combined: realistic JSON5 file")]
    public void Normalize_RealisticJson5()
    {
        var input = """
            {
              // Database configuration
              host: 'localhost',
              port: 0x1F90,
              maxRetries: +3,
              timeout: Infinity,
              "debug": true,
              tags: [
                'alpha',
                'beta',
              ],
            }
            """;

        var result = Json5Normalizer.Normalize(input);

        // Should be valid JSON — verify by parsing
        var parseResult = new JsonStructureParser().Parse(result);
        parseResult.Shape.Should().Be(JsonShape.NestedObject);

        var keys = parseResult.Keys.Select(k => k.Name).ToList();
        keys.Should().Contain("host");
        keys.Should().Contain("port");
        keys.Should().Contain("maxRetries");
        keys.Should().Contain("timeout");
        keys.Should().Contain("debug");
        keys.Should().Contain("tags");
    }

    [Test]
    [DisplayName("Empty string returns empty")]
    public void Normalize_EmptyString()
    {
        var result = Json5Normalizer.Normalize("");

        result.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Null input throws")]
    public void Normalize_NullThrows()
    {
        var act = () => Json5Normalizer.Normalize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    [DisplayName("Nested trailing commas in objects and arrays")]
    public void Normalize_NestedTrailingCommas()
    {
        const string input = """{"a":[1,2,],"b":{"x":1,},}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("""{"a":[1,2],"b":{"x":1}}""");
    }

    [Test]
    [DisplayName("Escape sequences in double-quoted strings pass through")]
    public void Normalize_EscapeSequencesPassThrough()
    {
        const string input = """{"path":"C:\\Users\\test","tab":"\t","newline":"\n"}""";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be(input);
    }

    [Test]
    [DisplayName("CRLF line endings in comments are preserved")]
    public void Normalize_CrlfInComment()
    {
        var input = "{\r\n  // comment\r\n  \"a\": 1\r\n}";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("{\r\n  \r\n  \"a\": 1\r\n}");
    }

    [Test]
    [DisplayName("Multi-line string with CRLF continuation")]
    public void Normalize_MultiLineStringCrlf()
    {
        var input = "{\"key\":\"line1\\\r\nline2\"}";

        var result = Json5Normalizer.Normalize(input);

        result.Should().Be("{\"key\":\"line1line2\"}");
    }
}
