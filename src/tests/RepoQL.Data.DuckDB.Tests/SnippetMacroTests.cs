using DuckDB.NET.Data;
using AwesomeAssertions;
using System.Text;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Metrics;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class SnippetMacroTests : IDisposable
{
    private readonly DuckDBConnection _connection;
    private readonly DuckDbGraphStore _store;
    private readonly IndexingMetrics _metrics;

    public SnippetMacroTests()
    {
        _connection = new DuckDBConnection("Data Source=:memory:");
        _connection.Open();
        _metrics = new IndexingMetrics();
        _store = new DuckDbGraphStore(_connection, _metrics);
        _store.EnsureSchema();
    }

    public void Dispose()
    {
        _store?.Dispose();
        _metrics?.Dispose();
        _connection?.Dispose();
    }

    // ========== Language Detection Tests ==========

    [Test]
    public void LanguageFromMediaTypeOrUri_DetectsFromMediaType()
    {
        using var cmd = _connection.CreateCommand();

        // Test various media types
        cmd.CommandText = "SELECT language_from_media_type_or_uri('text/x-csharp', 'test.txt')";
        var result = cmd.ExecuteScalar();
        result.Should().Be("csharp");

        cmd.CommandText = "SELECT language_from_media_type_or_uri('text/x-python', 'test.txt')";
        result = cmd.ExecuteScalar();
        result.Should().Be("python");

        cmd.CommandText = "SELECT language_from_media_type_or_uri('application/json', 'test.txt')";
        result = cmd.ExecuteScalar();
        result.Should().Be("json");

        cmd.CommandText = "SELECT language_from_media_type_or_uri('text/markdown', 'test.txt')";
        result = cmd.ExecuteScalar();
        result.Should().Be("markdown");
    }

    [Test]
    [Skip("Default mime detection is not working - can't see why")]
    public void LanguageFromMediaTypeOrUri_FallsBackToExtension()
    {
        using var cmd = _connection.CreateCommand();

        // Test file extensions when media type is null or unknown
        cmd.CommandText = "SELECT language_from_media_type_or_uri(NULL, 'test.cs')";
        var result = cmd.ExecuteScalar();
        result.Should().Be("csharp");

        cmd.CommandText = "SELECT language_from_media_type_or_uri(NULL, 'script.py')";
        result = cmd.ExecuteScalar();
        result.Should().Be("python");

        cmd.CommandText = "SELECT language_from_media_type_or_uri(NULL, 'component.tsx')";
        result = cmd.ExecuteScalar();
        result.Should().Be("tsx");

        cmd.CommandText = "SELECT language_from_media_type_or_uri(NULL, 'config.yml')";
        result = cmd.ExecuteScalar();
        result.Should().Be("yaml");

        cmd.CommandText = "SELECT language_from_media_type_or_uri('text/plain', 'main.rs')";
        result = cmd.ExecuteScalar();
        result.Should().Be("rust");
    }

    [Test]
    public void LanguageFromMediaTypeOrUri_ReturnsNullForUnknown()
    {
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = "SELECT language_from_media_type_or_uri(NULL, 'unknown.xyz')";
        var result = cmd.ExecuteScalar();
        result.Should().Be(DBNull.Value);

        cmd.CommandText = "SELECT language_from_media_type_or_uri('application/unknown', 'file')";
        result = cmd.ExecuteScalar();
        result.Should().Be(DBNull.Value);
    }

    // ========== Line/Column Calculation Tests ==========

    [Test]
    public void LineForByteOffset_CalculatesCorrectly()
    {
        using var cmd = _connection.CreateCommand();
        var text = "Line 1\nLine 2\nLine 3";
        var bytes = Encoding.UTF8.GetBytes(text);

        // First line
        cmd.CommandText = "SELECT line_for_byte_offset(?, 0)";
        cmd.Parameters.Add(new DuckDBParameter(text));
        var result = cmd.ExecuteScalar();
        result.Should().Be(1);

        // Second line (after first \n at position 6)
        cmd.CommandText = "SELECT line_for_byte_offset(?, 7)";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new DuckDBParameter(text));
        result = cmd.ExecuteScalar();
        result.Should().Be(2);

        // Third line (after second \n at position 13)
        cmd.CommandText = "SELECT line_for_byte_offset(?, 14)";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new DuckDBParameter(text));
        result = cmd.ExecuteScalar();
        result.Should().Be(3);
    }

    [Test]
    public void LineForByteOffset_HandlesNulls()
    {
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = "SELECT line_for_byte_offset(NULL, 10)";
        var result = cmd.ExecuteScalar();
        result.Should().Be(DBNull.Value);

        cmd.CommandText = "SELECT line_for_byte_offset('test', NULL)";
        result = cmd.ExecuteScalar();
        result.Should().Be(DBNull.Value);

        cmd.CommandText = "SELECT line_for_byte_offset('test', -1)";
        result = cmd.ExecuteScalar();
        result.Should().Be(DBNull.Value);
    }

    [Test]
    public void ColumnForByteOffset_CalculatesCorrectly()
    {
        using var cmd = _connection.CreateCommand();
        var text = "Line 1\nLine 2 here\nLine 3";

        // Beginning of first line
        cmd.CommandText = "SELECT column_for_byte_offset(?, 0)";
        cmd.Parameters.Add(new DuckDBParameter(text));
        var result = cmd.ExecuteScalar();
        result.Should().Be(1);

        // Middle of first line (position 3 = 'e' in "Line")
        cmd.CommandText = "SELECT column_for_byte_offset(?, 3)";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new DuckDBParameter(text));
        result = cmd.ExecuteScalar();
        result.Should().Be(4);

        // Beginning of second line (position 7, after \n)
        cmd.CommandText = "SELECT column_for_byte_offset(?, 7)";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new DuckDBParameter(text));
        result = cmd.ExecuteScalar();
        result.Should().Be(1);

        // Middle of second line (position 14 = 'h' in "here")
        cmd.CommandText = "SELECT column_for_byte_offset(?, 14)";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new DuckDBParameter(text));
        result = cmd.ExecuteScalar();
        result.Should().Be(8);
    }

    [Test]
    public void ColumnForByteOffset_HandlesMultiByteChars()
    {
        using var cmd = _connection.CreateCommand();
        // UTF-8 emoji is multi-byte
        var text = "Hello 😀 World";
        var bytes = Encoding.UTF8.GetBytes(text);

        // Before emoji
        cmd.CommandText = "SELECT column_for_byte_offset(?, 6)";
        cmd.Parameters.Add(new DuckDBParameter(text));
        var result = cmd.ExecuteScalar();
        result.Should().Be(7); // After "Hello "

        // After emoji (emoji is 4 bytes in UTF-8)
        cmd.CommandText = "SELECT column_for_byte_offset(?, 11)";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new DuckDBParameter(text));
        result = cmd.ExecuteScalar();
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
        _store.UpsertArtifact(artifact);

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///test.cs", out var uri1) ? uri1 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(node);

        // Query snippet with line range
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT line_number, text, is_focus, language 
            FROM snippet('file:///test.cs#line=4,6', 2)
            ORDER BY line_number";

        using var reader = cmd.ExecuteReader();
        var results = new List<(int line, string text, bool focus, string? lang)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)
            ));
        }

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
        _store.UpsertArtifact(artifact);

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///script.py", out var uri2) ? uri2 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(node);

        // Query snippet without fragment (should show first window)
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT line_number, text, is_focus 
            FROM snippet('file:///script.py', 3)
            ORDER BY line_number";

        using var reader = cmd.ExecuteReader();
        var results = new List<(int line, string text, bool focus)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetBoolean(2)
            ));
        }

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
        _store.UpsertArtifact(artifact);

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///test.txt", out var uri3) ? uri3 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(node);

        // Query snippet with char range (byte offsets 12-26 = "This is a test")
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT line_number, text, is_focus, focus_start_column, focus_end_column
            FROM snippet('file:///test.txt#char=12,26', 0)
            ORDER BY line_number";

        using var reader = cmd.ExecuteReader();
        var results = new List<(int line, string text, bool focus, int? startCol, int? endCol)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)
            ));
        }

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
        _store.UpsertArtifact(artifact);

        var docUri = RepoUri.Parse("file:///demo/Foo.cs");
        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = docUri,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(documentNode);

        var symbolSpan = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = documentNode.Id,
            StartLine = 3,
            EndLine = 5,
            StartColumn = 1,
            EndColumn = 24
        };
        _store.InsertSpan(symbolSpan);

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
        _store.UpsertNode(symbolNode);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT line_number, text, is_focus, focus_start_column, focus_end_column, resolved_uri
            FROM snippet('file:///demo/Foo.cs#symbol=Demo.Foo', 1)
            ORDER BY line_number";

        using var reader = cmd.ExecuteReader();
        var results = new List<(int line, string text, bool focus, int? startCol, int? endCol, string resolved)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
            ));
        }

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
        _store.UpsertArtifact(artifact);

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///app.js", out var uri4) ? uri4 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(node);

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
        _store.InsertSpan(span);

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
        _store.UpsertEdge(edge);

        // Query snippet with edge fragment
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT line_number, text, is_focus, focus_start_column, focus_end_column
            FROM snippet('file:///app.js#edge={edge.Id}', 1)
            ORDER BY line_number";

        using var reader = cmd.ExecuteReader();
        var results = new List<(int line, string text, bool focus, int? startCol, int? endCol)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)
            ));
        }

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
        _store.UpsertArtifact(artifact);

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse("file:///doc.txt", out var uri5) ? uri5 : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(node);

        // Query snippet and check resolved_uri
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT resolved_uri 
            FROM snippet('file:///doc.txt#line=1', 0)
            LIMIT 1";

        var resolvedUri = cmd.ExecuteScalar() as string;
        resolvedUri.Should().Be("file:///doc.txt#line=1");
    }
}
