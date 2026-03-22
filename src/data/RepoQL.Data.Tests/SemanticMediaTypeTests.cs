using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Data.Tests;

internal class SemanticMediaTypeTests
{
    // ========== Parsing Tests ==========

    [Test]
    public void Parse_SimpleMediaType_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("text/plain");

        mt.Type.Should().Be("text");
        mt.Subtype.Should().Be("plain");
        mt.Suffix.Should().BeNull();
        mt.Parameters.Should().BeEmpty();
    }

    [Test]
    public void Parse_MediaTypeWithSuffix_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("application/vnd.api+json");

        mt.Type.Should().Be("application");
        mt.Subtype.Should().Be("vnd.api");
        mt.Suffix.Should().Be("json");
    }

    [Test]
    public void Parse_MediaTypeWithCharset_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("text/html; charset=utf-8");

        mt.Type.Should().Be("text");
        mt.Subtype.Should().Be("html");
        mt.Charset.Should().Be("utf-8");
    }

    [Test]
    public void Parse_MediaTypeWithKind_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("application/json; kind=config.app");

        mt.Type.Should().Be("application");
        mt.Subtype.Should().Be("json");
        mt.Kind.Should().Be("config.app");
    }

    [Test]
    public void Parse_MediaTypeWithQuotedValue_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("application/json; profile=\"https://example.org/profile\"");

        mt.Type.Should().Be("application");
        mt.Subtype.Should().Be("json");
        mt.Profile?.ToString().Should().Be("https://example.org/profile");
    }

    [Test]
    public void Parse_MediaTypeWithEscapedQuotes_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("text/plain; desc=\"Contains \\\"quotes\\\" inside\"");

        var desc = mt.Parameters["desc"];
        desc.Should().Be("Contains \"quotes\" inside");
    }

    [Test]
    public void Parse_MediaTypeWithMultipleParameters_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("text/markdown; kind=markdown.doc; charset=utf-8; version=1.0");

        mt.Kind.Should().Be("markdown.doc");
        mt.Charset.Should().Be("utf-8");
        mt.Version.Should().Be("1.0");
    }

    [Test]
    public void Parse_ComplexExample_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse(
            "application/yaml; kind=openapi; version=3.1; profile=\"https://example.org/oas/3.1\"");

        mt.Type.Should().Be("application");
        mt.Subtype.Should().Be("yaml");
        mt.Kind.Should().Be("openapi");
        mt.Version.Should().Be("3.1");
        mt.Profile?.ToString().Should().Be("https://example.org/oas/3.1");
    }

    [Test]
    public void Parse_CaseInsensitive_NormalizesToLower()
    {
        var mt = SemanticMediaType.Parse("TEXT/HTML; CHARSET=UTF-8; KIND=Doc");

        mt.Type.Should().Be("text");
        mt.Subtype.Should().Be("html");
        mt.Charset.Should().Be("UTF-8"); // Values preserve case
        mt.Kind.Should().Be("Doc");
        mt.Parameters.Should().ContainKey("charset");
        mt.Parameters.Should().ContainKey("kind");
    }

    [Test]
    public void Parse_ParameterWithoutValue_ParsesAsNull()
    {
        var mt = SemanticMediaType.Parse("text/plain; compressed");

        mt.Parameters.Should().ContainKey("compressed");
        mt.Parameters["compressed"].Should().BeNull();
    }

    [Test]
    public void Parse_HandlesWhitespace_TrimsCorrectly()
    {
        var mt = SemanticMediaType.Parse("  text/plain  ;  charset  =  utf-8  ");

        mt.Type.Should().Be("text");
        mt.Subtype.Should().Be("plain");
        mt.Charset.Should().Be("utf-8");
    }

    // ========== TryParse Tests ==========

    [Test]
    public void TryParse_ValidInput_ReturnsTrue()
    {
        var success = SemanticMediaType.TryParse("text/plain", out var mt);

        success.Should().BeTrue();
        mt.Should().NotBeNull();
        mt!.Type.Should().Be("text");
    }

    [Test]
    public void TryParse_InvalidInput_ReturnsFalse()
    {
        var success = SemanticMediaType.TryParse("not-a-media-type", out var mt);

        success.Should().BeFalse();
        mt.Should().BeNull();
    }

    [Test]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        var success = SemanticMediaType.TryParse("", out var mt);

        success.Should().BeFalse();
        mt.Should().BeNull();
    }

    [Test]
    public void TryParse_NullString_ReturnsFalse()
    {
        var success = SemanticMediaType.TryParse(null!, out var mt);

        success.Should().BeFalse();
        mt.Should().BeNull();
    }

    [Test]
    public void Parse_InvalidFormat_ThrowsFormatException()
    {
        Action act = () => SemanticMediaType.Parse("invalid");

        act.Should().Throw<FormatException>()
            .WithMessage("*Invalid media type*");
    }

    // ========== ToString Tests ==========

    [Test]
    public void ToString_SimpleMediaType_FormatsCorrectly()
    {
        var mt = SemanticMediaType.Create("text", "plain");

        mt.ToString().Should().Be("text/plain");
    }

    [Test]
    public void ToString_WithSuffix_FormatsCorrectly()
    {
        var mt = SemanticMediaType.Create("application", "vnd.api", "json");

        mt.ToString().Should().Be("application/vnd.api+json");
    }

    [Test]
    public void ToString_WithTokenParameter_NoQuotes()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .WithCharset("utf-8");

        mt.ToString().Should().Be("text/plain;charset=utf-8");
    }

    [Test]
    public void ToString_WithNonTokenParameter_AddsQuotes()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .With("desc", "Has spaces");

        mt.ToString().Should().Be("text/plain;desc=\"Has spaces\"");
    }

    [Test]
    public void ToString_WithQuotesInValue_EscapesCorrectly()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .With("desc", "Has \"quotes\" inside");

        mt.ToString().Should().Be("text/plain;desc=\"Has \\\"quotes\\\" inside\"");
    }

    [Test]
    public void ToString_WithBackslashInValue_EscapesCorrectly()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .With("path", "C:\\folder\\file");

        mt.ToString().Should().Be("text/plain;path=\"C:\\\\folder\\\\file\"");
    }

    [Test]
    public void ToString_ParametersAreSorted()
    {
        var mt = SemanticMediaType.Create("text", "markdown")
            .WithVersion("1.0")
            .WithKind("doc")
            .WithCharset("utf-8");

        // Parameters should be alphabetically sorted
        mt.ToString().Should().Be("text/markdown;charset=utf-8;kind=doc;version=1.0");
    }

    [Test]
    public void ToString_RoundTrip_PreservesData()
    {
        var original = "application/json; kind=config.app; charset=utf-8; profile=\"https://example.org/profile\"";
        var mt = SemanticMediaType.Parse(original);
        var roundTrip = SemanticMediaType.Parse(mt.ToString());

        roundTrip.Type.Should().Be(mt.Type);
        roundTrip.Subtype.Should().Be(mt.Subtype);
        roundTrip.Kind.Should().Be(mt.Kind);
        roundTrip.Charset.Should().Be(mt.Charset);
        roundTrip.Profile?.ToString().Should().Be(mt.Profile?.ToString());
    }

    // ========== Builder Methods Tests ==========

    [Test]
    public void WithKind_SetsKindParameter()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .WithKind("markdown.doc");

        mt.Kind.Should().Be("markdown.doc");
    }

    [Test]
    public void WithKind_Null_RemovesParameter()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .WithKind("doc")
            .WithKind(null);

        mt.Kind.Should().BeNull();
        mt.Parameters.Should().NotContainKey("kind");
    }

    [Test]
    public void WithVersion_SetsVersionParameter()
    {
        var mt = SemanticMediaType.Create("application", "json")
            .WithVersion("2.0");

        mt.Version.Should().Be("2.0");
    }

    [Test]
    public void WithCharset_SetsCharsetParameter()
    {
        var mt = SemanticMediaType.Create("text", "html")
            .WithCharset("iso-8859-1");

        mt.Charset.Should().Be("iso-8859-1");
    }

    [Test]
    public void WithProfile_SetsProfileUri()
    {
        var uri = new Uri("https://example.org/profile");
        var mt = SemanticMediaType.Create("application", "json")
            .WithProfile(uri);

        mt.Profile.Should().Be(uri);
    }

    [Test]
    public void WithSchema_SetsSchemaUri()
    {
        var uri = new Uri("file:///schemas/app.schema.json");
        var mt = SemanticMediaType.Create("application", "json")
            .WithSchema(uri);

        mt.Schema.Should().Be(uri);
    }

    [Test]
    public void With_CustomParameter_AddsToParameters()
    {
        var mt = SemanticMediaType.Create("text", "plain")
            .With("custom", "value");

        mt.Parameters.Should().ContainKey("custom");
        mt.Parameters["custom"].Should().Be("value");
    }

    [Test]
    public void With_CreatesNewInstance_DoesNotModifyOriginal()
    {
        var original = SemanticMediaType.Create("text", "plain");
        var modified = original.WithKind("doc");

        original.Kind.Should().BeNull();
        modified.Kind.Should().Be("doc");
        ReferenceEquals(original, modified).Should().BeFalse();
    }

    // ========== URI Handling Tests ==========

    [Test]
    public void Profile_ParsesAbsoluteUri()
    {
        var mt = SemanticMediaType.Parse("application/json; profile=\"https://example.org/profile\"");

        mt.Profile.Should().NotBeNull();
        mt.Profile!.IsAbsoluteUri.Should().BeTrue();
        mt.Profile.ToString().Should().Be("https://example.org/profile");
    }

    [Test]
    public void Schema_ParsesFileUri()
    {
        var mt = SemanticMediaType.Parse("application/json; schema=\"file:///schemas/app.json\"");

        mt.Schema.Should().NotBeNull();
        mt.Schema!.Scheme.Should().Be("file");
    }

    [Test]
    public void Profile_InvalidUri_ReturnsNull()
    {
        var mt = SemanticMediaType.Parse("application/json; profile=\"not a valid uri\"");

        mt.Profile.Should().BeNull();
    }

    [Test]
    public void Schema_RelativeUri_ConvertsToAbsolute()
    {
        var mt = SemanticMediaType.Parse("""
                                         application/json; schema="/relative/path"
                                         """);

        // Relative URIs are converted to absolute file:// URIs
        mt.Schema.Should().NotBeNull();
        mt.Schema!.IsAbsoluteUri.Should().BeTrue();
        mt.Schema.Scheme.Should().Be("file");
    }

    // ========== Edge Cases ==========

    [Test]
    public void Parse_MultipleSuffixes_UsesLastOne()
    {
        var mt = SemanticMediaType.Parse("application/vnd.foo+bar+json");

        mt.Subtype.Should().Be("vnd.foo+bar");
        mt.Suffix.Should().Be("json");
    }

    [Test]
    public void Parse_EmptyParameter_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("text/plain; empty=");

        mt.Parameters.Should().ContainKey("empty");
        mt.Parameters["empty"].Should().Be("");
    }

    [Test]
    public void Parse_SemicolonInQuotedValue_ParsesCorrectly()
    {
        var mt = SemanticMediaType.Parse("text/plain; desc=\"Contains; semicolon\"");

        mt.Parameters["desc"].Should().Be("Contains; semicolon");
    }

    [Test]
    public void Parse_ConsecutiveSemicolons_IgnoresEmpty()
    {
        var mt = SemanticMediaType.Parse("text/plain;; charset=utf-8");

        mt.Charset.Should().Be("utf-8");
        mt.Parameters.Count.Should().Be(1);
    }

    [Test]
    public void Create_WithNullSuffix_CreatesWithoutSuffix()
    {
        var mt = SemanticMediaType.Create("application", "json", null);

        mt.Suffix.Should().BeNull();
        mt.ToString().Should().Be("application/json");
    }

    // ========== Token Detection Tests ==========

    [Test]
    public void ToString_ValidTokens_NoQuotes()
    {
        var validTokens = new[] { "abc", "123", "a-b", "a.b", "a_b", "a*b", "a+b", "a^b" };

        foreach (var token in validTokens)
        {
            var mt = SemanticMediaType.Create("text", "plain").With("param", token);
            var str = mt.ToString();

            str.Should().Be($"text/plain;param={token}");
        }
    }

    [Test]
    public void ToString_InvalidTokens_AddsQuotes()
    {
        var invalidTokens = new[] { "has space", "has,comma", "has(paren", "has[bracket", "has@at" };

        foreach (var value in invalidTokens)
        {
            var mt = SemanticMediaType.Create("text", "plain").With("param", value);
            var str = mt.ToString();

            str.Should().Contain($"param=\"");
        }
    }

    // ========== Spec Examples Tests ==========

    [Test]
    public void Parse_SpecExamples_AllParseCorrectly()
    {
        var examples = new[]
        {
            ("text/markdown; kind=markdown.doc; charset=utf-8", "markdown.doc", "utf-8"),
            ("application/json; kind=config.app; schema=\"file:///schemas/app.schema.json\"", "config.app", null),
            ("application/yaml; kind=openapi; version=3.1; profile=\"https://example.org/oas/3.1\"", "openapi", null),
            ("text/x-csharp; kind=cs.class; charset=utf-8", "cs.class", "utf-8"),
            ("text/x-python; kind=py.module; charset=utf-8", "py.module", "utf-8"),
            ("application/zip; kind=playwright.trace", "playwright.trace", null)
        };

        foreach (var (input, expectedKind, expectedCharset) in examples)
        {
            var mt = SemanticMediaType.Parse(input);

            mt.Kind.Should().Be(expectedKind);
            if (expectedCharset != null)
                mt.Charset.Should().Be(expectedCharset);
        }
    }
}