using RepoQL.Contracts;

namespace RepoQL.Data.Tests;

public class RepoQLUriTests
{
    #region Parse Tests - Section 5 of spec

    [Test]
    public async Task Parse_EmptyFragment_ReturnsContainerOnly()
    {
        // Arrange
        var input = "file:///repo/README.md";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.AbsoluteUri).IsEqualTo("file:///repo/README.md");
        await Assert.That(result.Fragment).IsEqualTo("");
        await Assert.That(result.Container.AbsoluteUri).IsEqualTo("file:///repo/README.md");
        await Assert.That(result.Loc.Raw).IsEqualTo("");
    }

    [Test]
    public async Task Parse_JsonPointer_StartingWithSlash()
    {
        // Arrange
        var input = "file:///api/openapi.yaml#/components/schemas/User";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.JsonPointer).IsEqualTo("/components/schemas/User");
        await Assert.That(result.Loc.GetJsonPointerSegments()).HasCount(3);
        await Assert.That(result.Loc.GetJsonPointerSegments()[0]).IsEqualTo("components");
        await Assert.That(result.Loc.GetJsonPointerSegments()[1]).IsEqualTo("schemas");
        await Assert.That(result.Loc.GetJsonPointerSegments()[2]).IsEqualTo("User");
    }

    [Test]
    public async Task Parse_JsonPointer_WithEscapedCharacters()
    {
        // Arrange
        var input = "file:///config.json#/paths/~1users~1{id}/get";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.JsonPointer).IsEqualTo("/paths/~1users~1{id}/get");
        var segments = result.Loc.GetJsonPointerSegments();
        await Assert.That(segments[0]).IsEqualTo("paths");
        await Assert.That(segments[1]).IsEqualTo("/users/{id}"); // ~1 decoded to /
        await Assert.That(segments[2]).IsEqualTo("get");
    }

    [Test]
    public async Task Parse_ParameterizedFragment_WithSymbolAndLine()
    {
        // Arrange
        var input = "file:///repo/lib.cs#symbol=Foo.Bar&line=12,20";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Symbol).IsEqualTo("Foo.Bar");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(12);
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(20);
    }

    [Test]
    public async Task Parse_ParameterizedFragment_WithPercentEncodedSymbol()
    {
        // Arrange
        var input = "file:///repo/lib.cs#symbol=Foo%3A%3ABar%3C%3E&line=5";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Symbol).IsEqualTo("Foo::Bar<>");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(5);
        await Assert.That(result.Loc.Line!.Value.End).IsNull();
    }

    [Test]
    public async Task Parse_SimpleLineRange_BothBounds()
    {
        // Arrange
        var input = "file:///repo/README.md#line=40,55";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Line!.Value.Start).IsEqualTo(40);
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(55);
    }

    [Test]
    public async Task Parse_SimpleLineRange_StartOnly()
    {
        // Arrange
        var input = "file:///repo/app.py#line=12";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Line!.Value.Start).IsEqualTo(12);
        await Assert.That(result.Loc.Line!.Value.End).IsNull();
    }

    [Test]
    public async Task Parse_SimpleLineRange_EndOnly()
    {
        // Arrange
        var input = "file:///repo/app.py#line=,20";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Line!.Value.Start).IsNull();
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(20);
    }

    [Test]
    public async Task Parse_SimpleCharRange_BothBounds()
    {
        // Arrange
        var input = "file:///repo/file.txt#char=100,150";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Char!.Value.Start).IsEqualTo(100);
        await Assert.That(result.Loc.Char!.Value.End).IsEqualTo(150);
    }

    [Test]
    public async Task Parse_PlainAnchor()
    {
        // Arrange
        var input = "file:///repo/README.md#installation";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Anchor).IsEqualTo("installation");
    }

    [Test]
    public async Task Parse_UnknownParameters_ArePreserved()
    {
        // Arrange
        var input = "file:///repo/doc.md#custom=value&another=123&line=5";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Parameters["custom"]).IsEqualTo("value");
        await Assert.That(result.Loc.Parameters["another"]).IsEqualTo("123");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_InvalidUri_ReturnsFalse()
    {
        // Arrange
        var input = "not a valid uri";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsFalse();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Parse_RelativeUri_ReturnsFalse()
    {
        // Arrange
        var input = "../relative/path.txt";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsFalse();
        await Assert.That(result).IsNull();
    }

    #endregion

    #region Builder Tests

    [Test]
    public async Task FromAnchor_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///repo/README.md");

        // Act
        var result = RepoUri.FromAnchor(container, "installation");

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///repo/README.md#installation");
        await Assert.That(result.Loc.Anchor).IsEqualTo("installation");
    }

    [Test]
    public async Task FromLines_BothBounds_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///repo/app.py");

        // Act
        var result = RepoUri.FromLines(container, 40, 55);

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///repo/app.py#line=40,55");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(40);
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(55);
    }

    [Test]
    public async Task FromLines_SingleLine_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///repo/app.py");

        // Act
        var result = RepoUri.FromLines(container, 12, null);

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///repo/app.py#line=12");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(12);
        await Assert.That(result.Loc.Line!.Value.End).IsNull();
    }

    [Test]
    public async Task FromChars_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///repo/file.txt");

        // Act
        var result = RepoUri.FromChars(container, 100, 150);

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///repo/file.txt#char=100,150");
        await Assert.That(result.Loc.Char!.Value.Start).IsEqualTo(100);
        await Assert.That(result.Loc.Char!.Value.End).IsEqualTo(150);
    }

    [Test]
    public async Task FromJsonPointer_String_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///api/openapi.yaml");

        // Act
        var result = RepoUri.FromJsonPointer(container, "/components/schemas/User");

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///api/openapi.yaml#/components/schemas/User");
        await Assert.That(result.Loc.JsonPointer).IsEqualTo("/components/schemas/User");
    }

    [Test]
    public async Task FromJsonPointer_StringWithoutLeadingSlash_AddsSlash()
    {
        // Arrange
        var container = new Uri("file:///api/openapi.yaml");

        // Act
        var result = RepoUri.FromJsonPointer(container, "components/schemas/User");

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///api/openapi.yaml#/components/schemas/User");
        await Assert.That(result.Loc.JsonPointer).IsEqualTo("/components/schemas/User");
    }

    [Test]
    public async Task FromJsonPointer_Segments_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///config.json");
        var segments = new[] { "servers", "0", "url" };

        // Act
        var result = RepoUri.FromJsonPointer(container, segments);

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///config.json#/servers/0/url");
        await Assert.That(result.Loc.JsonPointer).IsEqualTo("/servers/0/url");
    }

    [Test]
    public async Task FromJsonPointer_SegmentsWithSpecialChars_EncodesCorrectly()
    {
        // Arrange
        var container = new Uri("file:///config.json");
        var segments = new[] { "paths", "/users/{id}", "get" };

        // Act
        var result = RepoUri.FromJsonPointer(container, segments);

        // Assert
        // RFC 6901 allows curly braces in JSON pointer tokens, but URI encoding applies to the fragment
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///config.json#/paths/~1users~1%7Bid%7D/get");
        await Assert.That(result.Loc.JsonPointer).IsEqualTo("/paths/~1users~1{id}/get");
    }

    [Test]
    public async Task FromSymbol_WithLineRange_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///repo/lib.cs");

        // Act
        var result = RepoUri.FromSymbol(container, "Foo.Bar", 12, 20);

        // Assert
        await Assert.That(result.AbsoluteUri).IsEqualTo("file:///repo/lib.cs#line=12,20&symbol=Foo.Bar");
        await Assert.That(result.Loc.Symbol).IsEqualTo("Foo.Bar");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(12);
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(20);
    }

    [Test]
    public async Task FromSymbol_WithSpecialCharacters_EncodesCorrectly()
    {
        // Arrange
        var container = new Uri("file:///repo/lib.cs");

        // Act
        var result = RepoUri.FromSymbol(container, "Foo::Bar<T>", 5, null);

        // Assert
        await Assert.That(result.Fragment).Contains("symbol=Foo%3A%3ABar%3CT%3E");
        await Assert.That(result.Loc.Symbol).IsEqualTo("Foo::Bar<T>");
    }

    [Test]
    public async Task FromParams_CreatesCorrectUri()
    {
        // Arrange
        var container = new Uri("file:///repo/doc.md");
        var parameters = new Dictionary<string, string?>
        {
            { "custom", "value" },
            { "flag", null }
        };

        // Act
        var result = RepoUri.FromParams(container, parameters);

        // Assert
        await Assert.That(result.Fragment).Contains("custom=value");
        await Assert.That(result.Fragment).Contains("flag");
        await Assert.That(result.Loc.Parameters["custom"]).IsEqualTo("value");
        await Assert.That(result.Loc.Parameters["flag"]).IsNull();
    }

    [Test]
    public async Task Builder_NonAbsoluteContainer_Throws()
    {
        // Arrange
        var container = new Uri("/relative/path", UriKind.Relative);

        // Act & Assert
        await Assert.That(() => RepoUri.FromAnchor(container, "test"))
            .Throws<ArgumentException>();
    }

    #endregion

    #region Round-trip Tests - Section 8 of spec

    [Test]
    public async Task RoundTrip_PlainAnchor()
    {
        // Arrange
        var original = "file:///repo/README.md#installation";

        // Act
        var parsed = RepoUri.TryParse(original, out var uri);
        var rebuilt = RepoUri.FromAnchor(uri!.Container, uri.Loc.Anchor!);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(rebuilt.AbsoluteUri).IsEqualTo(original);
    }

    [Test]
    public async Task RoundTrip_JsonPointer()
    {
        // Arrange
        var original = "file:///api/openapi.yaml#/components/schemas/User";

        // Act
        var parsed = RepoUri.TryParse(original, out var uri);
        var rebuilt = RepoUri.FromJsonPointer(uri!.Container, uri.Loc.JsonPointer!);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(rebuilt.AbsoluteUri).IsEqualTo(original);
    }

    [Test]
    public async Task RoundTrip_LineRange()
    {
        // Arrange
        var original = "file:///repo/app.py#line=40,55";

        // Act
        var parsed = RepoUri.TryParse(original, out var uri);
        var rebuilt = RepoUri.FromLines(uri!.Container, uri.Loc.Line!.Value.Start, uri.Loc.Line!.Value.End);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(rebuilt.AbsoluteUri).IsEqualTo(original);
    }

    [Test]
    public async Task RoundTrip_SymbolWithLine_ParameterOrdering()
    {
        // Arrange
        var input = "file:///repo/lib.cs#symbol=Foo.Bar&line=12,20";

        // Act
        var parsed = RepoUri.TryParse(input, out var uri);
        var rebuilt = RepoUri.FromSymbol(uri!.Container, uri.Loc.Symbol!, uri.Loc.Line!.Value.Start, uri.Loc.Line!.Value.End);

        // Assert - parameters should be sorted lexicographically per spec
        await Assert.That(parsed).IsTrue();
        await Assert.That(rebuilt.AbsoluteUri).IsEqualTo("file:///repo/lib.cs#line=12,20&symbol=Foo.Bar");
    }

    #endregion

    #region Archive Container Tests - Section 1.2 of spec

    [Test]
    public async Task Parse_JarUri_SingleArchive()
    {
        // Arrange
        var input = "jar:file:///artifacts/trace.zip!/resources/network.log#line=1,200";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Container.AbsoluteUri).IsEqualTo("jar:file:///artifacts/trace.zip!/resources/network.log");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(1);
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(200);
    }

    [Test]
    public async Task Parse_JarUri_NestedArchive()
    {
        // Arrange
        var input = "jar:file:///a.zip!/b.zip!/c.txt";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Container.AbsoluteUri).IsEqualTo("jar:file:///a.zip!/b.zip!/c.txt");
        await Assert.That(result.Fragment).IsEqualTo("");
    }

    #endregion

    #region Edge Cases and Normalization - Section 3 of spec

    [Test]
    public async Task Parse_HttpsUri_WithFragment()
    {
        // Arrange
        var input = "https://example.com/api/spec.yaml#/paths/users/get";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Container.AbsoluteUri).IsEqualTo("https://example.com/api/spec.yaml");
        await Assert.That(result.Loc.JsonPointer).IsEqualTo("/paths/users/get");
    }

    [Test]
    public async Task Parse_FileUri_WithQuery_PreservesQuery()
    {
        // Arrange
        var input = "file:///repo/doc.md?version=1.2#line=5";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Container.AbsoluteUri).IsEqualTo("file:///repo/doc.md?version=1.2");
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_EmptyLineValue_ParsesCorrectly()
    {
        // Arrange
        var input = "file:///repo/app.py#line=";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        // Empty value creates a range with (null, null)
        await Assert.That(result!.Loc.Line!.Value.Start).IsNull();
        await Assert.That(result!.Loc.Line!.Value.End).IsNull();
    }

    [Test]
    public async Task Parse_CaseInsensitiveLinePrefix()
    {
        // Arrange
        var input = "file:///repo/app.py#LINE=5,10";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Line!.Value.Start).IsEqualTo(5);
        await Assert.That(result.Loc.Line!.Value.End).IsEqualTo(10);
    }

    [Test]
    public async Task Parse_ParameterWithoutValue()
    {
        // Arrange
        var input = "file:///repo/doc.md#flag&line=5";

        // Act
        var success = RepoUri.TryParse(input, out var result);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.Parameters.ContainsKey("flag")).IsTrue();
        await Assert.That(result.Loc.Parameters["flag"]).IsNull();
        await Assert.That(result.Loc.Line!.Value.Start).IsEqualTo(5);
    }

    #endregion

    #region Location Helper Tests

    [Test]
    public async Task Location_EncodeJsonPointerSegment()
    {
        // Arrange & Act & Assert
        await Assert.That(RepoUri.Location.EncodeJsonPointerSegment("simple")).IsEqualTo("simple");
        await Assert.That(RepoUri.Location.EncodeJsonPointerSegment("with/slash")).IsEqualTo("with~1slash");
        await Assert.That(RepoUri.Location.EncodeJsonPointerSegment("with~tilde")).IsEqualTo("with~0tilde");
        await Assert.That(RepoUri.Location.EncodeJsonPointerSegment("both~/chars")).IsEqualTo("both~0~1chars");
    }

    [Test]
    public async Task Location_DecodeJsonPointerSegment()
    {
        // Arrange & Act & Assert
        await Assert.That(RepoUri.Location.DecodeJsonPointerSegment("simple")).IsEqualTo("simple");
        await Assert.That(RepoUri.Location.DecodeJsonPointerSegment("with~1slash")).IsEqualTo("with/slash");
        await Assert.That(RepoUri.Location.DecodeJsonPointerSegment("with~0tilde")).IsEqualTo("with~tilde");
        await Assert.That(RepoUri.Location.DecodeJsonPointerSegment("both~0~1chars")).IsEqualTo("both~/chars");
    }

    [Test]
    public async Task Location_WithLineRange_ModifiesExisting()
    {
        // Arrange
        var loc = RepoUri.Location.FromAnchor("test");

        // Act
        var modified = loc.WithLineRange(10, 20);

        // Assert
        await Assert.That(modified.Anchor).IsEqualTo("test");
        await Assert.That(modified.Line!.Value.Start).IsEqualTo(10);
        await Assert.That(modified.Line!.Value.End).IsEqualTo(20);
    }

    [Test]
    public async Task Location_WithCharRange_ModifiesExisting()
    {
        // Arrange
        var loc = RepoUri.Location.FromSymbol("MyClass");

        // Act
        var modified = loc.WithCharRange(100, 200);

        // Assert
        await Assert.That(modified.Symbol).IsEqualTo("MyClass");
        await Assert.That(modified.Char!.Value.Start).IsEqualTo(100);
        await Assert.That(modified.Char!.Value.End).IsEqualTo(200);
    }

    #endregion

    #region Priority and Mutual Exclusivity Tests - Section 4 of spec

    [Test]
    public async Task Builder_JsonPointerTakesPrecedence_OverOtherFragmentTypes()
    {
        // When building with JSON pointer, it should take precedence
        var container = new Uri("file:///config.json");
        var result = RepoUri.FromJsonPointer(container, "/path/to/value");

        // The fragment should only contain the JSON pointer
        await Assert.That(result.Fragment).IsEqualTo("#/path/to/value");
        await Assert.That(result.Loc.JsonPointer).IsEqualTo("/path/to/value");
    }

    [Test]
    public async Task Parse_MultipleFragmentTypes_JsonPointerWins()
    {
        // If somehow a fragment has both patterns, JSON pointer (starting with /) takes precedence
        var input = "file:///config.json#/path/to/value";

        var success = RepoUri.TryParse(input, out var result);

        await Assert.That(success).IsTrue();
        await Assert.That(result!.Loc.JsonPointer).IsEqualTo("/path/to/value");
        // Should not be interpreted as parameters even if it contains '='
        await Assert.That(result.Loc.Parameters).IsEmpty();
    }

    #endregion

    #region Normalization Tests

    [Test]
    public async Task Normalize_RemovesControlCharsAndDuplicateSlashes()
    {
        var input = "file:///docs//proposals/implemented/Grammars.md\r\n";
        var normalized = RepoUri.Normalize(input);

        await Assert.That(normalized).IsEqualTo("file:///docs/proposals/implemented/Grammars.md");
    }

    [Test]
    public async Task Normalize_RejectsAbsoluteWindowsFileUri()
    {
        var input = "file:///C:/repo/file.txt";

        await Assert.That(() => RepoUri.Normalize(input))
            .Throws<ArgumentException>();
    }

    #endregion
}
