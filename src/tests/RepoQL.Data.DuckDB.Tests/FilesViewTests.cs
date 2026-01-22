using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB.Tests;

public class FilesViewTests
{
    [Test]
    [DisplayName("Files view returns all expected columns")]
    public void FilesView_ReturnsAllColumns()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/example.cs")!;
        db.IndexArtifact(uri, CreateTestArtifact(uri, "line1\nline2\nline3"));

        var rows = db.Read("""
            SELECT uri, source, path, dirname, name, extension,
                   lang, mime, byte_size, lines,
                   headline, summary, structure, mtime,
                   error_count, warning_count, node_id, artifact_id
            FROM files
            """, r => new
        {
            Uri = r.GetString(0),
            Source = r.GetString(1),
            Path = r.GetString(2),
            Dirname = r.IsDBNull(3) ? null : r.GetString(3),
            Name = r.GetString(4),
            Extension = r.IsDBNull(5) ? null : r.GetString(5),
            Lang = r.IsDBNull(6) ? null : r.GetString(6),
            Mime = r.IsDBNull(7) ? null : r.GetString(7),
            ByteSize = r.GetInt64(8),
            Lines = r.IsDBNull(9) ? (long?)null : r.GetInt64(9),
            Headline = r.IsDBNull(10) ? null : r.GetString(10),
            Summary = r.IsDBNull(11) ? null : r.GetString(11),
            Structure = r.IsDBNull(12) ? null : r.GetString(12),
            Mtime = r.GetDateTime(13),
            ErrorCount = r.GetInt64(14),
            WarningCount = r.GetInt64(15),
            NodeId = r.GetGuid(16),
            ArtifactId = r.GetGuid(17)
        });

        rows.Should().HaveCount(1);
        rows[0].Uri.Should().Contain("example.cs");
        rows[0].Name.Should().Be("example.cs");
        rows[0].Extension.Should().Be(".cs");
        rows[0].Lines.Should().Be(3);
        rows[0].ErrorCount.Should().Be(0);
        rows[0].WarningCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Source extraction works for file:// URIs")]
    public void FilesView_SourceExtraction_FileScheme()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/components/Button.tsx")!;
        db.IndexArtifact(uri, CreateTestArtifact(uri));

        var rows = db.Read("SELECT source, path, dirname FROM files", r => new
        {
            Source = r.GetString(0),
            Path = r.GetString(1),
            Dirname = r.IsDBNull(2) ? null : r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].Source.Should().Be("file://");
        rows[0].Path.Should().Contain("src/components/Button.tsx");
        rows[0].Dirname.Should().Contain("src/components");
    }

    [Test]
    [DisplayName("Source extraction works for repoql-docs:// URIs")]
    public void FilesView_SourceExtraction_RepoqlDocsScheme()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("repoql-docs:///quickstart.md")!;
        db.IndexArtifact(uri, CreateTestArtifact(uri));

        var rows = db.Read("SELECT source, name FROM files", r => new
        {
            Source = r.GetString(0),
            Name = r.GetString(1)
        });

        rows.Should().HaveCount(1);
        rows[0].Source.Should().Be("repoql-docs://");
        rows[0].Name.Should().Be("quickstart.md");
    }

    [Test]
    [DisplayName("Source extraction works for github:// URIs")]
    public void FilesView_SourceExtraction_GithubScheme()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("github://owner/repo/src/main.rs")!;
        db.IndexArtifact(uri, CreateTestArtifact(uri));

        var rows = db.Read("SELECT source, path, name FROM files", r => new
        {
            Source = r.GetString(0),
            Path = r.GetString(1),
            Name = r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].Source.Should().Be("github://owner/repo");
        rows[0].Name.Should().Be("main.rs");
    }

    [Test]
    [DisplayName("Line count is computed correctly from text content")]
    public void FilesView_LineCount_ComputedCorrectly()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test.txt")!;
        var content = "line1\nline2\nline3\nline4\nline5";
        db.IndexArtifact(uri, CreateTestArtifact(uri, content));

        var rows = db.Read("SELECT lines FROM files", r => r.GetInt64(0));

        rows.Should().HaveCount(1);
        rows[0].Should().Be(5);
    }

    [Test]
    [DisplayName("Line count handles single line file")]
    public void FilesView_LineCount_SingleLine()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///single.txt")!;
        db.IndexArtifact(uri, CreateTestArtifact(uri, "just one line"));

        var rows = db.Read("SELECT lines FROM files", r => r.GetInt64(0));

        rows.Should().HaveCount(1);
        rows[0].Should().Be(1);
    }

    [Test]
    [DisplayName("Annotation counts aggregate correctly")]
    public void FilesView_AnnotationCounts_AggregateCorrectly()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.cs")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact(uri));

        // Add annotations with different severities
        db.ReplaceAnnotations(uri, new List<Annotation>
        {
            new() { Kind = "lint", Severity = "error", Source = "analyzer",
                    Message = "Error 1", ScopeDocumentId = indexResult.DocumentId },
            new() { Kind = "lint", Severity = "error", Source = "analyzer",
                    Message = "Error 2", ScopeDocumentId = indexResult.DocumentId },
            new() { Kind = "lint", Severity = "warning", Source = "analyzer",
                    Message = "Warning 1", ScopeDocumentId = indexResult.DocumentId },
            new() { Kind = "lint", Severity = "info", Source = "analyzer",
                    Message = "Info 1", ScopeDocumentId = indexResult.DocumentId }
        });

        var rows = db.Read("SELECT error_count, warning_count FROM files", r => new
        {
            ErrorCount = r.GetInt64(0),
            WarningCount = r.GetInt64(1)
        });

        rows.Should().HaveCount(1);
        rows[0].ErrorCount.Should().Be(2);
        rows[0].WarningCount.Should().Be(1);
    }

    [Test]
    [DisplayName("Files without annotations have zero counts")]
    public void FilesView_NoAnnotations_ZeroCounts()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///clean.cs")!;
        db.IndexArtifact(uri, CreateTestArtifact(uri));

        var rows = db.Read("SELECT error_count, warning_count FROM files", r => new
        {
            ErrorCount = r.GetInt64(0),
            WarningCount = r.GetInt64(1)
        });

        rows.Should().HaveCount(1);
        rows[0].ErrorCount.Should().Be(0);
        rows[0].WarningCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Extension extraction handles various file types")]
    [Arguments("file:///test.cs", ".cs")]
    [Arguments("file:///test.component.tsx", ".tsx")]
    [Arguments("file:///Makefile", null)]
    [Arguments("file:///test.tar.gz", ".gz")]
    public void FilesView_ExtensionExtraction(string uriString, string? expectedExtension)
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse(uriString)!;
        db.IndexArtifact(uri, CreateTestArtifact(uri));

        var rows = db.Read("SELECT extension FROM files", r => r.IsDBNull(0) ? null : r.GetString(0));

        rows.Should().HaveCount(1);
        rows[0].Should().Be(expectedExtension);
    }

    [Test]
    [DisplayName("X-ray summaries are included")]
    public void FilesView_ExploreSummaries_Included()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///documented.cs")!;
        var artifact = CreateTestArtifactWithExplore(uri,
            headline: "A well-documented class",
            summary: "Contains utility methods for data processing",
            structure: "- Class DataProcessor\n  - Method Process()");
        db.IndexArtifact(uri, artifact);

        var rows = db.Read("SELECT headline, summary, structure FROM files", r => new
        {
            Headline = r.IsDBNull(0) ? null : r.GetString(0),
            Summary = r.IsDBNull(1) ? null : r.GetString(1),
            Structure = r.IsDBNull(2) ? null : r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].Headline.Should().Be("A well-documented class");
        rows[0].Summary.Should().Be("Contains utility methods for data processing");
        rows[0].Structure.Should().Contain("DataProcessor");
    }

    [Test]
    [DisplayName("Multiple files are returned correctly")]
    public void FilesView_MultipleFiles()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        db.IndexArtifact(RepoUri.Parse("file:///src/a.cs")!, CreateTestArtifact(RepoUri.Parse("file:///src/a.cs")!));
        db.IndexArtifact(RepoUri.Parse("file:///src/b.ts")!, CreateTestArtifact(RepoUri.Parse("file:///src/b.ts")!));
        db.IndexArtifact(RepoUri.Parse("repoql-docs:///readme.md")!, CreateTestArtifact(RepoUri.Parse("repoql-docs:///readme.md")!));

        var rows = db.Read("SELECT name, source FROM files ORDER BY name", r => new
        {
            Name = r.GetString(0),
            Source = r.GetString(1)
        });

        rows.Should().HaveCount(3);
        rows[0].Name.Should().Be("a.cs");
        rows[0].Source.Should().Be("file://");
        rows[1].Name.Should().Be("b.ts");
        rows[1].Source.Should().Be("file://");
        rows[2].Name.Should().Be("readme.md");
        rows[2].Source.Should().Be("repoql-docs://");
    }

    private static ParsedArtifact CreateTestArtifact(RepoUri uri, string? content = null)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var textContent = content ?? "test content";

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = textContent.Length,
                MediaType = SemanticMediaType.Parse("text/plain"),
                Text = textContent
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = "Test Document",
                Props = new JsonObject { ["title"] = "Test" }
            },
            Children = [],
            Spans = [],
            Edges = []
        };
    }

    private static ParsedArtifact CreateTestArtifactWithExplore(RepoUri uri, string? headline = null, string? summary = null, string? structure = null)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/plain"),
                Text = "test content",
                Headline = headline,
                Summary = summary,
                Structure = structure
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = headline ?? "Test Document",
                Props = new JsonObject { ["title"] = "Test" }
            },
            Children = [],
            Spans = [],
            Edges = []
        };
    }
}
