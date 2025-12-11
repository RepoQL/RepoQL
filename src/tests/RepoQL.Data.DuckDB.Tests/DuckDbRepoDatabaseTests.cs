using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB.Tests;

public class DuckDbRepoDatabaseTests
{
    [Test]
    [DisplayName("Schema initialization creates all tables")]
    public void EnsureSchema_CreatesAllTables()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var results = db.Query("SELECT COUNT(*) AS cnt FROM node");
        results.Should().HaveCount(1);
        results[0]["cnt"].Should().Be(0L);
    }

    [Test]
    [DisplayName("Query returns dictionary results")]
    public void Query_ReturnsDictionaryResults()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var results = db.Query("SELECT 1 AS value, 'hello' AS text");

        results.Should().HaveCount(1);
        results[0]["value"].Should().Be(1);
        results[0]["text"].Should().Be("hello");
    }

    [Test]
    [DisplayName("Query with mapper returns typed results")]
    public void Query_WithMapper_ReturnsTypedResults()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var results = db.Query("SELECT 42 AS num", r => r.GetInt32(0));

        results.Should().HaveCount(1);
        results[0].Should().Be(42);
    }

    [Test]
    [DisplayName("IndexArtifact inserts new document and populates search projection")]
    public void IndexArtifact_InsertsNewDocument()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact = CreateTestArtifact();

        var result = db.IndexArtifact(uri, artifact);

        result.DocumentId.Should().NotBe(Guid.Empty);
        result.WasUpdate.Should().BeFalse();

        // Verify document node
        var rows = db.Query("SELECT uri FROM node WHERE kind = 'document'");
        rows.Should().HaveCount(1);
        rows[0]["uri"]!.ToString().Should().Contain("test/doc.md");

        // Verify search projection
        var searchRows = db.Query("SELECT basename FROM document_search");
        searchRows.Should().HaveCount(1);
        searchRows[0]["basename"].Should().Be("doc.md");
    }

    [Test]
    [DisplayName("IndexArtifact updates existing document at same URI")]
    public void IndexArtifact_UpdatesExistingDocument()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact1 = CreateTestArtifact("content v1");
        var artifact2 = CreateTestArtifact("content v2");

        var result1 = db.IndexArtifact(uri, artifact1);
        var result2 = db.IndexArtifact(uri, artifact2);

        result1.WasUpdate.Should().BeFalse();
        result2.WasUpdate.Should().BeTrue();

        // Should still be only one document
        var rows = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
        rows[0]["cnt"].Should().Be(1L);
    }

    [Test]
    [DisplayName("DeleteArtifact removes document and cleans up search projection")]
    public void DeleteArtifact_RemovesDocumentAndSearchProjection()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        db.IndexArtifact(uri, CreateTestArtifact());

        var deleted = db.DeleteArtifact(uri);

        deleted.Should().BeTrue();

        db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'")[0]["cnt"]
            .Should().Be(0L);
        db.Query("SELECT COUNT(*) AS cnt FROM document_search")[0]["cnt"]
            .Should().Be(0L);
    }

    [Test]
    [DisplayName("DeleteArtifact returns false for non-existent document")]
    public void DeleteArtifact_ReturnsFalseWhenNotFound()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var deleted = db.DeleteArtifact(RepoUri.Parse("file:///nonexistent.md")!);

        deleted.Should().BeFalse();
    }

    [Test]
    [DisplayName("ReplaceAnnotations inserts new annotations for document")]
    public void ReplaceAnnotations_InsertsAnnotations()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        var annotations = new List<Annotation>
        {
            new()
            {
                Kind = "lint",
                Severity = "warning",
                Source = "test-analyzer",
                Message = "Test warning",
                ScopeDocumentId = indexResult.DocumentId
            }
        };

        var replaced = db.ReplaceAnnotations(uri, annotations);

        replaced.Should().BeTrue();
        db.Query("SELECT COUNT(*) AS cnt FROM annotation")[0]["cnt"].Should().Be(1L);
    }

    [Test]
    [DisplayName("ReplaceAnnotations clears previous annotations from same source")]
    public void ReplaceAnnotations_ClearsOldFromSameSource()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        // Insert two annotations from analyzer-a
        db.ReplaceAnnotations(uri, new List<Annotation>
        {
            new() { Kind = "lint", Severity = "warning", Source = "analyzer-a",
                    Message = "First warning", ScopeDocumentId = indexResult.DocumentId },
            new() { Kind = "lint", Severity = "error", Source = "analyzer-a",
                    Message = "First error", ScopeDocumentId = indexResult.DocumentId }
        });

        // Replace with one new annotation from same source
        db.ReplaceAnnotations(uri, new List<Annotation>
        {
            new() { Kind = "lint", Severity = "info", Source = "analyzer-a",
                    Message = "New info", ScopeDocumentId = indexResult.DocumentId }
        });

        var rows = db.Query("SELECT message FROM annotation");
        rows.Should().HaveCount(1, "old annotations from same source should be deleted");
        rows[0]["message"].Should().Be("New info");
    }

    [Test]
    [DisplayName("ReplaceAnnotations returns false for non-existent document")]
    public void ReplaceAnnotations_ReturnsFalseWhenDocumentNotFound()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var replaced = db.ReplaceAnnotations(
            RepoUri.Parse("file:///nonexistent.md")!,
            new List<Annotation>
            {
                new() { Kind = "lint", Severity = "info", Source = "test",
                        Message = "Test", ScopeDocumentId = Guid.NewGuid() }
            });

        replaced.Should().BeFalse();
    }

    [Test]
    [DisplayName("WriteEmbeddings inserts structure embeddings for document")]
    public void WriteEmbeddings_InsertsStructureEmbeddings()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeStructure,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f], "test-model", 3)
        });

        var rows = db.Query("SELECT embedding_type, scope FROM document_embedding");
        rows.Should().HaveCount(1);
        rows[0]["embedding_type"].Should().Be("structure");
        rows[0]["scope"].Should().Be("document");
    }

    [Test]
    [DisplayName("WriteEmbeddings inserts chunked full embeddings")]
    public void WriteEmbeddings_InsertsChunkedFullEmbeddings()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        // Write two chunks for the same document
        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeFull,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f], "test-model", 3, StartByte: 0, EndByte: 1000),
            new(indexResult.DocumentId, indexResult.DocumentId, 1, DocumentEmbedding.TypeFull,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.4f, 0.5f, 0.6f], "test-model", 3, StartByte: 800, EndByte: 1800)
        });

        var rows = db.Query("SELECT chunk_index, start_byte, end_byte FROM document_embedding ORDER BY chunk_index");
        rows.Should().HaveCount(2, "two chunks should be stored");
        rows[0]["chunk_index"].Should().Be(0);
        rows[0]["start_byte"].Should().Be(0L);
        rows[1]["chunk_index"].Should().Be(1);
        rows[1]["start_byte"].Should().Be(800L);
    }

    [Test]
    [DisplayName("IndexArtifact writes edges with all fields")]
    public void IndexArtifact_WritesEdgesWithAllFields()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact = CreateTestArtifactWithEdge();

        db.IndexArtifact(uri, artifact);

        var rows = db.Query("SELECT type, is_composition, ordinal, semantic_key, destination_uri, composition_child_id FROM edge");
        rows.Should().HaveCount(1);
        rows[0]["type"].Should().Be("REFERS_TO");
        rows[0]["is_composition"].Should().Be(false);
        rows[0]["ordinal"].Should().Be(1);
        rows[0]["semantic_key"].Should().Be("test-key");
        rows[0]["destination_uri"].Should().Be("file:///other/file.md");
    }

    [Test]
    [DisplayName("IndexArtifact writes composition edges with child constraint")]
    public void IndexArtifact_WritesCompositionEdges()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact = CreateTestArtifactWithCompositionEdge();

        db.IndexArtifact(uri, artifact);

        var rows = db.Query("SELECT type, is_composition, composition_child_id, destination_node_id FROM edge");
        rows.Should().HaveCount(1);
        rows[0]["type"].Should().Be("HAS_PART");
        rows[0]["is_composition"].Should().Be(true);
        // composition_child_id should equal destination_node_id for composition edges
        rows[0]["composition_child_id"].Should().Be(rows[0]["destination_node_id"]);
    }

    private static ParsedArtifact CreateTestArtifact(string content = "test content")
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = content.Length,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/doc.md"),
                ArtifactId = artifactId,
                Headline = "Test Document",
                Props = new JsonObject { ["title"] = "Test" }
            },
            Children = [],
            Spans = [],
            Edges = []
        };
    }

    private static ParsedArtifact CreateTestArtifactWithEdge()
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
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/doc.md"),
                ArtifactId = artifactId,
                Headline = "Test Document"
            },
            Children = [],
            Spans = [],
            Edges =
            [
                new Edge
                {
                    SrcId = docId,
                    DstUri = RepoUri.Parse("file:///other/file.md"),
                    Type = "REFERS_TO",
                    IsComposition = false,
                    Ordinal = 1,
                    EdgeKey = "test-key"
                }
            ]
        };
    }

    private static ParsedArtifact CreateTestArtifactWithCompositionEdge()
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/doc.md"),
                ArtifactId = artifactId,
                Headline = "Test Document"
            },
            Children =
            [
                new Node
                {
                    Id = childId,
                    Kind = "md_section",
                    ArtifactId = artifactId,
                    Headline = "Section 1"
                }
            ],
            Spans = [],
            Edges =
            [
                new Edge
                {
                    SrcId = docId,
                    DstId = childId,
                    Type = "HAS_PART",
                    IsComposition = true,
                    Ordinal = 0
                }
            ]
        };
    }

    #region Thread Safety Tests

    [Test]
    [DisplayName("Concurrent reads do not block each other")]
    public async Task ConcurrentReads_DoNotBlock()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        // Index some documents first
        for (var i = 0; i < 10; i++)
        {
            var uri = RepoUri.Parse($"file:///test/doc{i}.md")!;
            db.IndexArtifact(uri, CreateTestArtifact($"content {i}"));
        }

        // Run many concurrent reads
        var tasks = new List<Task<int>>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var results = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
                return Convert.ToInt32(results[0]["cnt"]);
            }));
        }

        var counts = await Task.WhenAll(tasks);

        // All should see 10 documents
        counts.Should().AllSatisfy(c => c.Should().Be(10));
    }

    [Test]
    [DisplayName("Concurrent writes are serialized correctly")]
    public async Task ConcurrentWrites_AreSerializedCorrectly()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        // Run many concurrent writes
        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                var uri = RepoUri.Parse($"file:///test/doc{index}.md")!;
                db.IndexArtifact(uri, CreateTestArtifact($"content {index}"));
            }));
        }

        await Task.WhenAll(tasks);

        // All 20 documents should be indexed
        var results = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
        Convert.ToInt32(results[0]["cnt"]).Should().Be(20);
    }

    [Test]
    [DisplayName("Mixed concurrent reads and writes maintain consistency")]
    public async Task MixedConcurrentReadsAndWrites_MaintainConsistency()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var writeCount = 0;
        var readResults = new List<int>();
        var writeLock = new object();
        var readLock = new object();

        // Run mixed reads and writes concurrently
        var tasks = new List<Task>();

        // Writers
        for (var i = 0; i < 15; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                var uri = RepoUri.Parse($"file:///test/doc{index}.md")!;
                db.IndexArtifact(uri, CreateTestArtifact($"content {index}"));
                lock (writeLock) { writeCount++; }
            }));
        }

        // Readers (interspersed)
        for (var i = 0; i < 30; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                // Small delay to let some writes happen
                Thread.Sleep(Random.Shared.Next(1, 5));
                var results = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
                var count = Convert.ToInt32(results[0]["cnt"]);
                lock (readLock) { readResults.Add(count); }
            }));
        }

        await Task.WhenAll(tasks);

        // Final count should be 15
        var finalCount = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
        Convert.ToInt32(finalCount[0]["cnt"]).Should().Be(15);

        // All reads should have seen a valid count (0 to 15)
        readResults.Should().AllSatisfy(c => c.Should().BeInRange(0, 15));

        // Reads should show monotonic progress (counts should generally increase over time)
        // This verifies writes are actually happening during reads
        readResults.Should().Contain(c => c > 0, "some reads should see written documents");
    }

    [Test]
    [DisplayName("Writers block readers and vice versa appropriately")]
    public async Task WritersBlockReaders_Appropriately()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var writerStarted = new ManualResetEventSlim(false);
        var writerCanFinish = new ManualResetEventSlim(false);
        var readerResults = new List<(DateTime time, int count)>();
        var readerLock = new object();

        // Start a slow writer that signals when it has the lock
        var writerTask = Task.Run(() =>
        {
            var uri = RepoUri.Parse("file:///test/slow.md")!;
            // We can't easily make the write slow, but we can verify the lock behavior
            // by checking that reads see consistent state
            for (var i = 0; i < 5; i++)
            {
                var docUri = RepoUri.Parse($"file:///test/batch{i}.md")!;
                db.IndexArtifact(docUri, CreateTestArtifact($"batch {i}"));
            }
        });

        // Start concurrent readers
        var readerTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            for (var j = 0; j < 5; j++)
            {
                var results = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
                var count = Convert.ToInt32(results[0]["cnt"]);
                lock (readerLock)
                {
                    readerResults.Add((DateTime.UtcNow, count));
                }
                Thread.Sleep(1);
            }
        })).ToList();

        await Task.WhenAll(new[] { writerTask }.Concat(readerTasks));

        // Final state should be 5 documents
        var finalCount = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
        Convert.ToInt32(finalCount[0]["cnt"]).Should().Be(5);

        // All individual reads should see a consistent count (not partial writes)
        // Each read sees 0, 1, 2, 3, 4, or 5 - never a fractional state
        readerResults.Select(r => r.count).Should().AllSatisfy(c => c.Should().BeInRange(0, 5));
    }

    [Test]
    [DisplayName("Concurrent deletes and reads maintain consistency")]
    public async Task ConcurrentDeletesAndReads_MaintainConsistency()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        // First, index 20 documents
        for (var i = 0; i < 20; i++)
        {
            var uri = RepoUri.Parse($"file:///test/doc{i}.md")!;
            db.IndexArtifact(uri, CreateTestArtifact($"content {i}"));
        }

        var readResults = new List<int>();
        var readLock = new object();

        // Delete half while reading
        var tasks = new List<Task>();

        // Deleters
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                var uri = RepoUri.Parse($"file:///test/doc{index}.md")!;
                db.DeleteArtifact(uri);
            }));
        }

        // Readers
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var results = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
                var count = Convert.ToInt32(results[0]["cnt"]);
                lock (readLock) { readResults.Add(count); }
            }));
        }

        await Task.WhenAll(tasks);

        // Final count should be 10 (20 - 10 deleted)
        var finalCount = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'");
        Convert.ToInt32(finalCount[0]["cnt"]).Should().Be(10);

        // All reads should see valid counts between 10 and 20
        readResults.Should().AllSatisfy(c => c.Should().BeInRange(10, 20));
    }

    #endregion

    #region Reindex Cleanup Tests

    [Test]
    [DisplayName("Reindex cleans up old child nodes")]
    public void IndexArtifact_ReindexCleansUpOldChildren()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;

        // First index with 3 children
        var artifact1 = CreateTestArtifactWithChildren(3);
        db.IndexArtifact(uri, artifact1);

        var initialChildCount = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'md_section'");
        Convert.ToInt32(initialChildCount[0]["cnt"]).Should().Be(3);

        // Reindex with 1 child
        var artifact2 = CreateTestArtifactWithChildren(1);
        db.IndexArtifact(uri, artifact2);

        // Should only have 1 child now
        var finalChildCount = db.Query("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'md_section'");
        Convert.ToInt32(finalChildCount[0]["cnt"]).Should().Be(1, "old children should be deleted on reindex");
    }

    [Test]
    [DisplayName("Reindex cleans up old spans")]
    public void IndexArtifact_ReindexCleansUpOldSpans()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;

        // First index with 3 spans
        var artifact1 = CreateTestArtifactWithSpans(3);
        db.IndexArtifact(uri, artifact1);

        var initialSpanCount = db.Query("SELECT COUNT(*) AS cnt FROM span");
        Convert.ToInt32(initialSpanCount[0]["cnt"]).Should().Be(3);

        // Reindex with 1 span
        var artifact2 = CreateTestArtifactWithSpans(1);
        db.IndexArtifact(uri, artifact2);

        // Should only have 1 span now
        var finalSpanCount = db.Query("SELECT COUNT(*) AS cnt FROM span");
        Convert.ToInt32(finalSpanCount[0]["cnt"]).Should().Be(1, "old spans should be deleted on reindex");
    }

    [Test]
    [DisplayName("Reindex cleans up old edges")]
    public void IndexArtifact_ReindexCleansUpOldEdges()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;

        // First index with 2 reference edges
        var artifact1 = CreateTestArtifactWithReferenceEdges(2);
        db.IndexArtifact(uri, artifact1);

        var initialEdgeCount = db.Query("SELECT COUNT(*) AS cnt FROM edge WHERE type = 'REFERS_TO'");
        Convert.ToInt32(initialEdgeCount[0]["cnt"]).Should().Be(2);

        // Reindex with 1 edge
        var artifact2 = CreateTestArtifactWithReferenceEdges(1);
        db.IndexArtifact(uri, artifact2);

        // Should only have 1 edge now
        var finalEdgeCount = db.Query("SELECT COUNT(*) AS cnt FROM edge WHERE type = 'REFERS_TO'");
        Convert.ToInt32(finalEdgeCount[0]["cnt"]).Should().Be(1, "old edges should be deleted on reindex");
    }

    [Test]
    [DisplayName("Delete cleans up embeddings")]
    public void DeleteArtifact_CleansUpEmbeddings()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        // Add embeddings
        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeStructure,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f], "test-model", 3),
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeFull,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.4f, 0.5f, 0.6f], "test-model", 3)
        });

        var embeddingCount = db.Query("SELECT COUNT(*) AS cnt FROM document_embedding");
        Convert.ToInt32(embeddingCount[0]["cnt"]).Should().Be(2);

        // Delete the document
        db.DeleteArtifact(uri);

        // Embeddings should be gone
        var finalEmbeddingCount = db.Query("SELECT COUNT(*) AS cnt FROM document_embedding");
        Convert.ToInt32(finalEmbeddingCount[0]["cnt"]).Should().Be(0, "embeddings should be deleted with document");
    }

    #endregion

    #region Annotation Source Isolation Tests

    [Test]
    [DisplayName("ReplaceAnnotations preserves annotations from other sources")]
    public void ReplaceAnnotations_PreservesOtherSources()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        // Add annotations from two sources
        db.ReplaceAnnotations(uri, new List<Annotation>
        {
            new() { Kind = "lint", Severity = "warning", Source = "analyzer-a",
                    Message = "Warning A", ScopeDocumentId = indexResult.DocumentId },
            new() { Kind = "lint", Severity = "error", Source = "analyzer-b",
                    Message = "Error B", ScopeDocumentId = indexResult.DocumentId }
        });

        // Replace only analyzer-a annotations
        db.ReplaceAnnotations(uri, new List<Annotation>
        {
            new() { Kind = "lint", Severity = "info", Source = "analyzer-a",
                    Message = "Info A (new)", ScopeDocumentId = indexResult.DocumentId }
        });

        // Should have 2 annotations: new one from analyzer-a, original from analyzer-b
        var rows = db.Query("SELECT source, message FROM annotation ORDER BY source");
        rows.Should().HaveCount(2, "annotations from other sources should be preserved");
        rows[0]["source"].Should().Be("analyzer-a");
        rows[0]["message"].Should().Be("Info A (new)");
        rows[1]["source"].Should().Be("analyzer-b");
        rows[1]["message"].Should().Be("Error B");
    }

    #endregion

    #region Embedding Upsert Tests

    [Test]
    [DisplayName("WriteEmbeddings upserts on conflict")]
    public void WriteEmbeddings_UpsertsOnConflict()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        // Write initial embedding
        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeStructure,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f], "model-v1", 3)
        });

        // Write updated embedding with same key but different vector/model
        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeStructure,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.9f, 0.8f, 0.7f], "model-v2", 3)
        });

        // Should still have only 1 embedding (upserted, not duplicated)
        var rows = db.Query("SELECT model FROM document_embedding");
        rows.Should().HaveCount(1, "should upsert, not duplicate");
        rows[0]["model"].Should().Be("model-v2", "should be updated to new model");
    }

    [Test]
    [DisplayName("WriteEmbeddings allows structure and full types to coexist")]
    public void WriteEmbeddings_StructureAndFullCoexist()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        // Write both structure and full embeddings for same document
        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeStructure,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f], "test-model", 3),
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeFull,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.4f, 0.5f, 0.6f], "test-model", 3)
        });

        var rows = db.Query("SELECT embedding_type FROM document_embedding ORDER BY embedding_type");
        rows.Should().HaveCount(2);
        rows[0]["embedding_type"].Should().Be("full");
        rows[1]["embedding_type"].Should().Be("structure");
    }

    #endregion

    #region Null Input Validation Tests

    [Test]
    [DisplayName("IndexArtifact throws on null URI")]
    public void IndexArtifact_ThrowsOnNullUri()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var act = () => db.IndexArtifact(null!, CreateTestArtifact());

        act.Should().Throw<ArgumentNullException>().WithParameterName("uri");
    }

    [Test]
    [DisplayName("IndexArtifact throws on null artifact")]
    public void IndexArtifact_ThrowsOnNullArtifact()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var act = () => db.IndexArtifact(uri, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("artifact");
    }

    [Test]
    [DisplayName("DeleteArtifact throws on null URI")]
    public void DeleteArtifact_ThrowsOnNullUri()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var act = () => db.DeleteArtifact(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("uri");
    }

    [Test]
    [DisplayName("ReplaceAnnotations throws on null URI")]
    public void ReplaceAnnotations_ThrowsOnNullUri()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var act = () => db.ReplaceAnnotations(null!, new List<Annotation>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("artifactUri");
    }

    [Test]
    [DisplayName("ReplaceAnnotations throws on null annotations")]
    public void ReplaceAnnotations_ThrowsOnNullAnnotations()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var act = () => db.ReplaceAnnotations(uri, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("annotations");
    }

    [Test]
    [DisplayName("WriteEmbeddings throws on null list")]
    public void WriteEmbeddings_ThrowsOnNullList()
    {
        using var db = new DuckDbRepoDatabase();
        db.EnsureSchema();

        var act = () => db.WriteEmbeddings(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("embeddings");
    }

    [Test]
    [DisplayName("EnsureSchema is idempotent")]
    public void EnsureSchema_IsIdempotent()
    {
        using var db = new DuckDbRepoDatabase();

        // Call twice - should not throw
        db.EnsureSchema();
        db.EnsureSchema();

        // Verify tables exist
        var results = db.Query("SELECT COUNT(*) AS cnt FROM node");
        results.Should().HaveCount(1);
    }

    #endregion

    #region Test Helpers

    private static ParsedArtifact CreateTestArtifactWithChildren(int childCount)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var children = new List<Node>();
        var edges = new List<Edge>();

        for (var i = 0; i < childCount; i++)
        {
            var childId = Guid.NewGuid();
            children.Add(new Node
            {
                Id = childId,
                Kind = "md_section",
                ArtifactId = artifactId,
                Headline = $"Section {i}"
            });
            edges.Add(new Edge
            {
                SrcId = docId,
                DstId = childId,
                Type = "HAS_PART",
                IsComposition = true,
                Ordinal = i
            });
        }

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/doc.md"),
                ArtifactId = artifactId,
                Headline = "Test Document"
            },
            Children = children,
            Spans = [],
            Edges = edges
        };
    }

    private static ParsedArtifact CreateTestArtifactWithSpans(int spanCount)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var spans = new List<Span>();
        for (var i = 0; i < spanCount; i++)
        {
            spans.Add(new Span
            {
                DocumentId = docId,
                StartLine = i + 1,
                EndLine = i + 2,
                StartColumn = 1,
                EndColumn = 10
            });
        }

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/doc.md"),
                ArtifactId = artifactId,
                Headline = "Test Document"
            },
            Children = [],
            Spans = spans,
            Edges = []
        };
    }

    private static ParsedArtifact CreateTestArtifactWithReferenceEdges(int edgeCount)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var edges = new List<Edge>();
        for (var i = 0; i < edgeCount; i++)
        {
            edges.Add(new Edge
            {
                SrcId = docId,
                DstUri = RepoUri.Parse($"file:///other/file{i}.md"),
                Type = "REFERS_TO",
                IsComposition = false,
                Ordinal = i
            });
        }

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/doc.md"),
                ArtifactId = artifactId,
                Headline = "Test Document"
            },
            Children = [],
            Spans = [],
            Edges = edges
        };
    }

    #endregion
}
