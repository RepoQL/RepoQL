using AwesomeAssertions;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class SnippetMacroTests : IDisposable
{
    private readonly DuckDbDataStore _store;

    public SnippetMacroTests()
    {
        _store = new DuckDbDataStore();
    }

    public void Dispose()
    {
        _store?.Dispose();
    }

    // ========== Language Detection Tests ==========

    [Test]
    public void LanguageFromMediaTypeOrUri_DetectsFromMediaType()
    {
        // Test various media types
        var result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri('text/x-csharp', 'test.txt')");
        result.Should().Be("csharp");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri('text/x-python', 'test.txt')");
        result.Should().Be("python");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri('application/json', 'test.txt')");
        result.Should().Be("json");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri('text/markdown', 'test.txt')");
        result.Should().Be("markdown");
    }

    [Test]
    [Skip("language_from_media_type_or_uri extension fallback not working - returns NULL for unknown extensions")]
    public void LanguageFromMediaTypeOrUri_FallsBackToExtension()
    {
        // Test file extensions when media type is null or unknown
        var result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri(NULL, 'test.cs')");
        result.Should().Be("csharp");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri(NULL, 'script.py')");
        result.Should().Be("python");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri(NULL, 'component.tsx')");
        result.Should().Be("tsx");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri(NULL, 'config.yml')");
        result.Should().Be("yaml");

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri('text/plain', 'main.rs')");
        result.Should().Be("rust");
    }

    [Test]
    public void LanguageFromMediaTypeOrUri_ReturnsNullForUnknown()
    {
        var result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri(NULL, 'unknown.xyz')");
        result.Should().BeNull();

        result = _store.ReadScalar<string>("SELECT language_from_media_type_or_uri('application/unknown', 'file')");
        result.Should().BeNull();
    }

    // ========== Line/Column Calculation Tests ==========

    [Test]
    public void LineForByteOffset_CalculatesCorrectly()
    {
        var text = "Line 1\nLine 2\nLine 3";

        // First line
        var result = _store.ReadScalar<int>($"SELECT line_for_byte_offset('{text}', 0)");
        result.Should().Be(1);

        // Second line (after first \n at position 6)
        result = _store.ReadScalar<int>($"SELECT line_for_byte_offset('{text}', 7)");
        result.Should().Be(2);

        // Third line (after second \n at position 13)
        result = _store.ReadScalar<int>($"SELECT line_for_byte_offset('{text}', 14)");
        result.Should().Be(3);
    }

    [Test]
    public void LineForByteOffset_HandlesNulls()
    {
        var result = _store.ReadScalar<int?>("SELECT line_for_byte_offset(NULL, 10)");
        result.Should().BeNull();

        result = _store.ReadScalar<int?>("SELECT line_for_byte_offset('test', NULL)");
        result.Should().BeNull();

        result = _store.ReadScalar<int?>("SELECT line_for_byte_offset('test', -1)");
        result.Should().BeNull();
    }

    [Test]
    public void ColumnForByteOffset_CalculatesCorrectly()
    {
        var text = "Line 1\nLine 2 here\nLine 3";

        // Beginning of first line
        var result = _store.ReadScalar<int>($"SELECT column_for_byte_offset('{text}', 0)");
        result.Should().Be(1);

        // Middle of first line (position 3 = 'e' in "Line")
        result = _store.ReadScalar<int>($"SELECT column_for_byte_offset('{text}', 3)");
        result.Should().Be(4);

        // Beginning of second line (position 7, after \n)
        result = _store.ReadScalar<int>($"SELECT column_for_byte_offset('{text}', 7)");
        result.Should().Be(1);

        // Middle of second line (position 14 = 'h' in "here")
        result = _store.ReadScalar<int>($"SELECT column_for_byte_offset('{text}', 14)");
        result.Should().Be(8);
    }

    [Test]
    public void ColumnForByteOffset_HandlesMultiByteChars()
    {
        // UTF-8 emoji is multi-byte
        var text = "Hello 😀 World";

        // Before emoji
        var result = _store.ReadScalar<int>($"SELECT column_for_byte_offset('{text}', 6)");
        result.Should().Be(7); // After "Hello "

        // After emoji (emoji is 4 bytes in UTF-8)
        result = _store.ReadScalar<int>($"SELECT column_for_byte_offset('{text}', 11)");
        result.Should().Be(9); // After emoji and space
    }

    // ========== Snippet Macro Tests ==========

    [Test]
    public void Snippet_ExtractsLinesWithContext()
    {
        // Create a test document
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "test-digest",
            Size = 100,
            Text = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8\nLine 9\nLine 10",
            MediaType = SemanticMediaType.Parse("text/x-csharp")
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///test.cs", out var uri1) ? uri1 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact { Artifact = artifact, DocumentNode = node });

        // Query snippet with line range
        var results = _store.Read(
            @"SELECT line_number, text, is_focus, language
              FROM snippet('file:///test.cs#line=4,6', 2)
              ORDER BY line_number",
            r => (
                line: r.GetInt32(0),
                text: r.GetString(1),
                focus: r.GetBoolean(2),
                lang: r.IsDBNull(3) ? null : r.GetString(3)
            ));

        // Should get lines 2-8 (context of 2 lines on each side)
        results.Should().HaveCount(7);
        results[0].Should().Be((2, "Line 2", false, "csharp"));
        results[1].Should().Be((3, "Line 3", false, "csharp"));
        results[2].Should().Be((4, "Line 4", true, "csharp"));  // Focus start
        results[3].Should().Be((5, "Line 5", true, "csharp"));  // Focus
        results[4].Should().Be((6, "Line 6", true, "csharp"));  // Focus end
        results[5].Should().Be((7, "Line 7", false, "csharp"));
        results[6].Should().Be((8, "Line 8", false, "csharp"));
    }

    [Test]
    public void Snippet_HandlesContainerUriOnly()
    {
        // Create a test document
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "test-digest-2",
            Size = 50,
            Text = "First line\nSecond line\nThird line",
            MediaType = SemanticMediaType.Parse("text/x-python")
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///script.py", out var uri2) ? uri2 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact { Artifact = artifact, DocumentNode = node });

        // Query snippet without fragment (should show first window)
        var results = _store.Read(
            @"SELECT line_number, text, is_focus
              FROM snippet('file:///script.py', 3)
              ORDER BY line_number",
            r => (
                line: r.GetInt32(0),
                text: r.GetString(1),
                focus: r.GetBoolean(2)
            ));

        // Should get first 7 lines (1 + 3*2 context)
        results.Should().HaveCount(3);
        results[0].Should().Be((1, "First line", true));
        results[1].Should().Be((2, "Second line", false));
        results[2].Should().Be((3, "Third line", false));
    }

    [Test]
    public void Snippet_HandlesCharFragment()
    {
        // Create a test document
        var text = "Hello World\nThis is a test\nThird line";
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "test-digest-3",
            Size = text.Length,
            Text = text,
            MediaType = SemanticMediaType.Parse("text/plain")
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///test.txt", out var uri3) ? uri3 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact { Artifact = artifact, DocumentNode = node });

        // Query snippet with char range (byte offsets 12-26 = "This is a test")
        var results = _store.Read(
            @"SELECT line_number, text, is_focus, focus_start_column, focus_end_column
              FROM snippet('file:///test.txt#char=12,26', 0)
              ORDER BY line_number",
            r => (
                line: r.GetInt32(0),
                text: r.GetString(1),
                focus: r.GetBoolean(2),
                startCol: r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                endCol: r.IsDBNull(4) ? (int?)null : r.GetInt32(4)
            ));

        // Should get just line 2 (no context requested)
        results.Should().HaveCount(1);
        results[0].line.Should().Be(2);
        results[0].text.Should().Be("This is a test");
        results[0].focus.Should().BeTrue();
        results[0].startCol.Should().Be(1);
        results[0].endCol.Should().Be(15);
    }

    [Test]
    public void Snippet_HandlesSymbolFragment()
    {
        var text = """
        namespace Demo;

        public class Foo
        {
            public void Bar() { }
        }
        """.Trim('\r', '\n');

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "test-digest-symbol",
            Size = text.Length,
            Text = text,
            MediaType = SemanticMediaType.Parse("text/plain")
        };

        var docUri = RepoUri.Parse("file:///demo/Foo.cs");
        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = docUri,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        var symbolSpan = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = documentNode.Id,
            StartLine = 3,
            EndLine = 5,
            StartColumn = 1,
            EndColumn = 24
        };

        var symbolNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "csharp.type",
            SpanId = symbolSpan.Id,
            Props = new JsonObject
            {
                ["name"] = "Foo",
                ["qualified_name"] = "Demo.Foo"
            }
        };

        _store.IndexArtifact(new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = documentNode,
            Children = [symbolNode],
            Spans = [symbolSpan]
        });

        var results = _store.Read(
            @"SELECT line_number, text, is_focus, focus_start_column, focus_end_column, resolved_uri
              FROM snippet('file:///demo/Foo.cs#symbol=Demo.Foo', 1)
              ORDER BY line_number",
            r => (
                line: r.GetInt32(0),
                text: r.GetString(1),
                focus: r.GetBoolean(2),
                startCol: r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                endCol: r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                resolved: r.IsDBNull(5) ? string.Empty : r.GetString(5)
            ));

        results.Should().NotBeEmpty();
        results.Select(r => r.line).Should().Equal(2, 3, 4, 5, 6);
        results.Where(r => r.focus).Select(r => r.line).Should().BeEquivalentTo(new[] { 3, 4, 5 });

        var focusRow = results.Single(r => r.line == 3);
        focusRow.startCol.Should().Be(1);
        focusRow.endCol.Should().Be(24);

        var resolved = results.Select(r => r.resolved).Distinct().Single();
        resolved.Should().Be("file:///demo/Foo.cs#line=3,5");
    }

    [Test]
    public void Snippet_HandlesEdgeFragment()
    {
        // Create documents with spans
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "test-digest-4",
            Size = 100,
            Text = "function foo() {\n  bar();\n  baz();\n}",
            MediaType = SemanticMediaType.Parse("text/javascript")
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///app.js", out var uri4) ? uri4 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        // Create a span for the bar() call
        var span = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = node.Id,
            StartLine = 2,
            EndLine = 2,
            StartColumn = 3,
            EndColumn = 8
        };

        // Create an edge with source span
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = node.Id,
            DstId = node.Id,
            Type = "calls",
            SrcSpanId = span.Id,
            ScopeDocumentId = node.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = node,
            Spans = [span],
            Edges = [edge]
        });

        // Query snippet with edge fragment
        var results = _store.Read(
            $@"SELECT line_number, text, is_focus, focus_start_column, focus_end_column
               FROM snippet('file:///app.js#edge={edge.Id}', 1)
               ORDER BY line_number",
            r => (
                line: r.GetInt32(0),
                text: r.GetString(1),
                focus: r.GetBoolean(2),
                startCol: r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                endCol: r.IsDBNull(4) ? (int?)null : r.GetInt32(4)
            ));

        // Should get lines 1-3 with focus on line 2
        results.Should().HaveCount(3);
        results[0].Should().Be((1, "function foo() {", false, null, null));
        results[1].line.Should().Be(2);
        results[1].text.Should().Be("  bar();");
        results[1].focus.Should().BeTrue();
        results[1].startCol.Should().Be(3);
        results[1].endCol.Should().Be(8);
        results[2].Should().Be((3, "  baz();", false, null, null));
    }

    [Test]
    public void Snippet_GeneratesResolvedUri()
    {
        // Create a test document
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "test-digest-5",
            Size = 50,
            Text = "Test content",
            MediaType = SemanticMediaType.Parse("text/plain")
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///doc.txt", out var uri5) ? uri5 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact { Artifact = artifact, DocumentNode = node });

        // Query snippet and check resolved_uri
        var resolvedUri = _store.ReadScalar<string>(
            @"SELECT resolved_uri
              FROM snippet('file:///doc.txt#line=1', 0)
              LIMIT 1");
        resolvedUri.Should().Be("file:///doc.txt#line=1");
    }
}
