using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Contracts.Snapshots;
using RepoQL.Data.DuckDB.Snapshots;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class SnapshotLoaderTests
{
    [Test]
    [DisplayName("LoadSource inserts documents into the store")]
    public void LoadSource_InsertsDocuments()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDoc("test:///doc1.md", "Hello"), CreateDoc("test:///doc2.md", "World")]);

        SnapshotLoader.LoadSource(db, source);

        var docs = db.Read("SELECT uri FROM node WHERE kind = 'document' ORDER BY uri",
            r => r.GetString(0));
        docs.Should().HaveCount(2);
        docs[0].Should().Be("test:///doc1.md");
        docs[1].Should().Be("test:///doc2.md");
    }

    [Test]
    [DisplayName("LoadSource stores version in metadata")]
    public void LoadSource_StoresVersion()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDoc("test:///doc.md", "content")]);

        SnapshotLoader.LoadSource(db, source);

        var version = db.ReadMetadataValue("snapshot:test-src");
        version.Should().Be("1.0");
    }

    [Test]
    [DisplayName("LoadSource skips when version matches")]
    public void LoadSource_SkipsWhenVersionMatches()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDoc("test:///doc.md", "content")]);

        SnapshotLoader.LoadSource(db, source);
        var countAfterFirstLoad = db.Read("SELECT COUNT(*) FROM artifact", r => r.GetInt64(0))[0];

        // Load again with same version — should skip
        var source2 = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDoc("test:///other.md", "different")]);
        SnapshotLoader.LoadSource(db, source2);

        var countAfterSecondLoad = db.Read("SELECT COUNT(*) FROM artifact", r => r.GetInt64(0))[0];
        countAfterSecondLoad.Should().Be(countAfterFirstLoad);

        // Original doc should still be there, not the new one
        var docs = db.Read("SELECT uri FROM node WHERE kind = 'document'", r => r.GetString(0));
        docs.Should().HaveCount(1);
        docs[0].Should().Be("test:///doc.md");
    }

    [Test]
    [DisplayName("LoadSource reloads when version changes")]
    public void LoadSource_ReloadsWhenVersionChanges()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source1 = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDoc("test:///old.md", "old content")]);
        SnapshotLoader.LoadSource(db, source1);

        // Load with new version
        var source2 = new TestSnapshotSource("test-src", "2.0", "test://",
            [CreateDoc("test:///new.md", "new content")]);
        SnapshotLoader.LoadSource(db, source2);

        // Old doc should be gone, new doc should be present
        var docs = db.Read("SELECT uri FROM node WHERE kind = 'document'", r => r.GetString(0));
        docs.Should().HaveCount(1);
        docs[0].Should().Be("test:///new.md");

        // Version should be updated
        var version = db.ReadMetadataValue("snapshot:test-src");
        version.Should().Be("2.0");
    }

    [Test]
    [DisplayName("LoadSource writes artifact text content")]
    public void LoadSource_WritesArtifactTextContent()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDoc("test:///doc.md", "# My Document\n\nHello world")]);

        SnapshotLoader.LoadSource(db, source);

        var texts = db.Read("SELECT text_content FROM artifact", r => r.IsDBNull(0) ? null : r.GetString(0));
        texts.Should().HaveCount(1);
        texts[0].Should().Contain("Hello world");
    }

    [Test]
    [DisplayName("LoadSource writes x-ray summaries")]
    public void LoadSource_WritesXRaySummaries()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDocWithXRay("test:///doc.md", "Test headline", "Test summary", "Test structure")]);

        SnapshotLoader.LoadSource(db, source);

        var headlines = db.Read("SELECT headline FROM artifact WHERE headline IS NOT NULL",
            r => r.GetString(0));
        headlines.Should().HaveCount(1);
        headlines[0].Should().Be("Test headline");
    }

    [Test]
    [DisplayName("LoadSource writes child nodes")]
    public void LoadSource_WritesChildNodes()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDocWithChildren("test:///doc.md")]);

        SnapshotLoader.LoadSource(db, source);

        var kinds = db.Read("SELECT kind FROM node ORDER BY kind", r => r.GetString(0));
        kinds.Should().Contain("document");
        kinds.Should().Contain("md_section");
    }

    [Test]
    [DisplayName("LoadSource writes edges")]
    public void LoadSource_WritesEdges()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDocWithEdges("test:///doc.md")]);

        SnapshotLoader.LoadSource(db, source);

        var edges = db.Read("SELECT type FROM edge", r => r.GetString(0));
        edges.Should().HaveCount(1);
        edges[0].Should().Be("HAS_PART");
    }

    [Test]
    [DisplayName("LoadSource writes annotations")]
    public void LoadSource_WritesAnnotations()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDocWithAnnotations("test:///doc.md")]);

        SnapshotLoader.LoadSource(db, source);

        var annotations = db.Read("SELECT message FROM annotation", r => r.GetString(0));
        annotations.Should().HaveCount(1);
        annotations[0].Should().Be("Test annotation");
    }

    [Test]
    [DisplayName("LoadSource writes spans")]
    public void LoadSource_WritesSpans()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var source = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDocWithSpans("test:///doc.md")]);

        SnapshotLoader.LoadSource(db, source);

        var spans = db.Read("SELECT start_line, end_line FROM span",
            r => (r.IsDBNull(0) ? (int?)null : r.GetInt32(0), r.IsDBNull(1) ? (int?)null : r.GetInt32(1)));
        spans.Should().HaveCount(1);
        spans[0].Should().Be((1, 10));
    }

    [Test]
    [DisplayName("LoadAll loads multiple sources")]
    public void LoadAll_LoadsMultipleSources()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var sources = new ISnapshotSource[]
        {
            new TestSnapshotSource("src-a", "1.0", "alpha://", [CreateDoc("alpha:///doc.md", "A")]),
            new TestSnapshotSource("src-b", "1.0", "beta://", [CreateDoc("beta:///doc.md", "B")])
        };

        SnapshotLoader.LoadAll(db, sources);

        var docs = db.Read("SELECT uri FROM node WHERE kind = 'document' ORDER BY uri",
            r => r.GetString(0));
        docs.Should().HaveCount(2);
        docs[0].Should().Be("alpha:///doc.md");
        docs[1].Should().Be("beta:///doc.md");
    }

    [Test]
    [DisplayName("LoadAll continues after single source failure")]
    public void LoadAll_ContinuesAfterFailure()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        var sources = new ISnapshotSource[]
        {
            new FailingSnapshotSource(),
            new TestSnapshotSource("src-ok", "1.0", "ok://", [CreateDoc("ok:///doc.md", "OK")])
        };

        SnapshotLoader.LoadAll(db, sources);

        // Second source should still have loaded
        var docs = db.Read("SELECT uri FROM node WHERE kind = 'document'", r => r.GetString(0));
        docs.Should().HaveCount(1);
        docs[0].Should().Be("ok:///doc.md");
    }

    [Test]
    [DisplayName("Version change cleans up old data completely")]
    public void LoadSource_VersionChange_CleansUpCompletely()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();


        // Load v1 with full graph
        var source1 = new TestSnapshotSource("test-src", "1.0", "test://",
            [CreateDocWithAnnotations("test:///doc.md")]);
        SnapshotLoader.LoadSource(db, source1);

        // Verify data exists
        db.Read("SELECT COUNT(*) FROM annotation", r => r.GetInt64(0))[0].Should().Be(1);

        // Load v2 with different doc (no annotations)
        var source2 = new TestSnapshotSource("test-src", "2.0", "test://",
            [CreateDoc("test:///new.md", "new")]);
        SnapshotLoader.LoadSource(db, source2);

        // Old annotations should be cleaned up
        db.Read("SELECT COUNT(*) FROM annotation", r => r.GetInt64(0))[0].Should().Be(0);
    }

    // ---- Helpers ----

    private static SnapshotDocument CreateDoc(string uri, string text)
    {
        var artifactId = Guid.NewGuid();
        var docNodeId = Guid.NewGuid();

        return new SnapshotDocument
        {
            Uri = RepoUri.Parse(uri),
            Records = new Records
            {
                Artifacts =
                [
                    new Artifact
                    {
                        Id = artifactId,
                        Digest = $"sha256:{Guid.NewGuid():N}",
                        Size = text.Length,
                        MediaType = SemanticMediaType.Parse("text/markdown"),
                        Text = text
                    }
                ],
                Nodes =
                [
                    new Node
                    {
                        Id = docNodeId,
                        Kind = "document",
                        Uri = RepoUri.Parse(uri),
                        ArtifactId = artifactId
                    }
                ]
            }
        };
    }

    private static SnapshotDocument CreateDocWithXRay(string uri, string headline, string summary, string structure)
    {
        var artifactId = Guid.NewGuid();
        var docNodeId = Guid.NewGuid();

        return new SnapshotDocument
        {
            Uri = RepoUri.Parse(uri),
            Records = new Records
            {
                Artifacts =
                [
                    new Artifact
                    {
                        Id = artifactId,
                        Digest = $"sha256:{Guid.NewGuid():N}",
                        Size = 100,
                        Headline = headline,
                        Summary = summary,
                        Structure = structure
                    }
                ],
                Nodes =
                [
                    new Node
                    {
                        Id = docNodeId,
                        Kind = "document",
                        Uri = RepoUri.Parse(uri),
                        ArtifactId = artifactId
                    }
                ]
            }
        };
    }

    private static SnapshotDocument CreateDocWithChildren(string uri)
    {
        var artifactId = Guid.NewGuid();
        var docNodeId = Guid.NewGuid();
        var childNodeId = Guid.NewGuid();

        return new SnapshotDocument
        {
            Uri = RepoUri.Parse(uri),
            Records = new Records
            {
                Artifacts =
                [
                    new Artifact
                    {
                        Id = artifactId,
                        Digest = $"sha256:{Guid.NewGuid():N}",
                        Size = 100
                    }
                ],
                Nodes =
                [
                    new Node
                    {
                        Id = docNodeId,
                        Kind = "document",
                        Uri = RepoUri.Parse(uri),
                        ArtifactId = artifactId
                    },
                    new Node
                    {
                        Id = childNodeId,
                        Kind = "md_section",
                        ArtifactId = artifactId,
                        Headline = "Section 1"
                    }
                ]
            }
        };
    }

    private static SnapshotDocument CreateDocWithEdges(string uri)
    {
        var artifactId = Guid.NewGuid();
        var docNodeId = Guid.NewGuid();
        var childNodeId = Guid.NewGuid();

        return new SnapshotDocument
        {
            Uri = RepoUri.Parse(uri),
            Records = new Records
            {
                Artifacts =
                [
                    new Artifact
                    {
                        Id = artifactId,
                        Digest = $"sha256:{Guid.NewGuid():N}",
                        Size = 100
                    }
                ],
                Nodes =
                [
                    new Node
                    {
                        Id = docNodeId,
                        Kind = "document",
                        Uri = RepoUri.Parse(uri),
                        ArtifactId = artifactId
                    },
                    new Node
                    {
                        Id = childNodeId,
                        Kind = "md_section",
                        ArtifactId = artifactId
                    }
                ],
                Edges =
                [
                    new Edge
                    {
                        SrcId = docNodeId,
                        DstId = childNodeId,
                        Type = "HAS_PART",
                        IsComposition = true
                    }
                ]
            }
        };
    }

    private static SnapshotDocument CreateDocWithSpans(string uri)
    {
        var artifactId = Guid.NewGuid();
        var docNodeId = Guid.NewGuid();

        return new SnapshotDocument
        {
            Uri = RepoUri.Parse(uri),
            Records = new Records
            {
                Artifacts =
                [
                    new Artifact
                    {
                        Id = artifactId,
                        Digest = $"sha256:{Guid.NewGuid():N}",
                        Size = 100
                    }
                ],
                Nodes =
                [
                    new Node
                    {
                        Id = docNodeId,
                        Kind = "document",
                        Uri = RepoUri.Parse(uri),
                        ArtifactId = artifactId
                    }
                ],
                Spans =
                [
                    new Span
                    {
                        DocumentId = docNodeId,
                        StartLine = 1,
                        EndLine = 10
                    }
                ]
            }
        };
    }

    private static SnapshotDocument CreateDocWithAnnotations(string uri)
    {
        var artifactId = Guid.NewGuid();
        var docNodeId = Guid.NewGuid();

        return new SnapshotDocument
        {
            Uri = RepoUri.Parse(uri),
            Records = new Records
            {
                Artifacts =
                [
                    new Artifact
                    {
                        Id = artifactId,
                        Digest = $"sha256:{Guid.NewGuid():N}",
                        Size = 100
                    }
                ],
                Nodes =
                [
                    new Node
                    {
                        Id = docNodeId,
                        Kind = "document",
                        Uri = RepoUri.Parse(uri),
                        ArtifactId = artifactId
                    }
                ],
                Annotations =
                [
                    new Annotation
                    {
                        Kind = "lint",
                        Severity = "info",
                        Source = "test-analyzer",
                        Message = "Test annotation",
                        ScopeDocumentId = docNodeId
                    }
                ],
                AnnotationSources = ["test-analyzer"]
            }
        };
    }

    private class TestSnapshotSource(string id, string version, string uriPrefix, IReadOnlyList<SnapshotDocument> docs)
        : ISnapshotSource
    {
        public string Id => id;
        public string Version => version;
        public string UriPrefix => uriPrefix;
        public IReadOnlyList<SnapshotDocument> GetDocuments() => docs;
    }

    private class FailingSnapshotSource : ISnapshotSource
    {
        public string Id => "fail";
        public string Version => "1.0";
        public string UriPrefix => "fail://";
        public IReadOnlyList<SnapshotDocument> GetDocuments() => throw new InvalidOperationException("Intentional test failure");
    }
}
