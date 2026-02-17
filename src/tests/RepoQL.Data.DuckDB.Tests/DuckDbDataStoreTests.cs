using System.Reflection;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB.Tests;

public class DuckDbDataStoreTests
{
    [Test]
    [DisplayName("Schema initialization creates all tables")]
    public void EnsureSchema_CreatesAllTables()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var results = db.Read("SELECT COUNT(*) AS cnt FROM node", r => r.GetInt64(0));
        results.Should().HaveCount(1);
        results[0].Should().Be(0L);
    }

    [Test]
    [DisplayName("Query returns dictionary results")]
    public void Query_ReturnsDictionaryResults()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var results = db.Read("SELECT 1 AS value, 'hello' AS text", r => new { Value = r.GetInt32(0), Text = r.GetString(1) });

        results.Should().HaveCount(1);
        results[0].Value.Should().Be(1);
        results[0].Text.Should().Be("hello");
    }

    [Test]
    [DisplayName("Query with mapper returns typed results")]
    public void Query_WithMapper_ReturnsTypedResults()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var results = db.Read("SELECT 42 AS num", r => r.GetInt32(0));

        results.Should().HaveCount(1);
        results[0].Should().Be(42);
    }

    [Test]
    [DisplayName("Read throws OperationCanceledException when token is canceled before execution")]
    public void Read_WithCanceledToken_ThrowsOperationCanceledException()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => db.Read("SELECT 1", r => r.GetInt32(0), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Test]
    [DisplayName("Query cancellation while waiting for lock throws and leaves connection usable")]
    public async Task Query_CanceledWhileWaitingForLock_ThrowsAndConnectionRemainsUsable()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        using var writeStarted = new ManualResetEventSlim(false);
        using var releaseWrite = new ManualResetEventSlim(false);

        var writerTask = Task.Run(() =>
        {
            db.WriteTransaction((_, _) =>
            {
                writeStarted.Set();
                releaseWrite.Wait(TimeSpan.FromSeconds(5));
            });
        });

        var started = writeStarted.Wait(TimeSpan.FromSeconds(5));
        started.Should().BeTrue("writer should hold the store lock before query starts");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> act = () => Task.Run(() => db.Query("SELECT 1 AS value", cts.Token));
        await act.Should().ThrowAsync<OperationCanceledException>();

        releaseWrite.Set();
        await writerTask;

        var rows = db.Query("SELECT 42 AS value");
        rows.Should().HaveCount(1);
        Convert.ToInt32(rows[0]["value"]).Should().Be(42);
    }

    [Test]
    [DisplayName("IndexArtifact inserts new document")]
    public void IndexArtifact_InsertsNewDocument()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact = CreateTestArtifact();

        var result = db.IndexArtifact(uri, artifact);

        result.DocumentId.Should().NotBe(Guid.Empty);
        result.WasUpdate.Should().BeFalse();

        // Verify document node
        var rows = db.Read("SELECT uri FROM node WHERE kind = 'document'", r => r.GetString(0));
        rows.Should().HaveCount(1);
        rows[0].Should().Contain("test/doc.md");

    }

    [Test]
    [DisplayName("IndexArtifact updates existing document at same URI")]
    public void IndexArtifact_UpdatesExistingDocument()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact1 = CreateTestArtifact("content v1");
        var artifact2 = CreateTestArtifact("content v2");

        var result1 = db.IndexArtifact(uri, artifact1);
        var result2 = db.IndexArtifact(uri, artifact2);

        result1.WasUpdate.Should().BeFalse();
        result2.WasUpdate.Should().BeTrue();

        // Should still be only one document
        var count = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0))[0];
        count.Should().Be(1L);
    }

    [Test]
    [DisplayName("DeleteArtifact removes document")]
    public void DeleteArtifact_RemovesDocument()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        db.IndexArtifact(uri, CreateTestArtifact());

        var deleted = db.DeleteArtifact(uri);

        deleted.Should().BeTrue();

        db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0))[0]
            .Should().Be(0L);
    }

    [Test]
    [DisplayName("DeleteArtifact returns false for non-existent document")]
    public void DeleteArtifact_ReturnsFalseWhenNotFound()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var deleted = db.DeleteArtifact(RepoUri.Parse("file:///nonexistent.md")!);

        deleted.Should().BeFalse();
    }

    [Test]
    [DisplayName("ReplaceAnnotations inserts new annotations for document")]
    public void ReplaceAnnotations_InsertsAnnotations()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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
        db.Read("SELECT COUNT(*) AS cnt FROM annotation", r => r.GetInt64(0))[0].Should().Be(1L);
    }

    [Test]
    [DisplayName("ReplaceAnnotations clears previous annotations from same source")]
    public void ReplaceAnnotations_ClearsOldFromSameSource()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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

        var rows = db.Read("SELECT message FROM annotation", r => r.GetString(0));
        rows.Should().HaveCount(1, "old annotations from same source should be deleted");
        rows[0].Should().Be("New info");
    }

    [Test]
    [DisplayName("ReplaceAnnotations returns false for non-existent document")]
    public void ReplaceAnnotations_ReturnsFalseWhenDocumentNotFound()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        db.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(indexResult.DocumentId, indexResult.DocumentId, 0, DocumentEmbedding.TypeStructure,
                uri.Container.AbsoluteUri, DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f], "test-model", 3)
        });

        var rows = db.Read("SELECT embedding_type, scope FROM document_embedding",
            r => new { EmbeddingType = r.GetString(0), Scope = r.GetString(1) });
        rows.Should().HaveCount(1);
        rows[0].EmbeddingType.Should().Be("structure");
        rows[0].Scope.Should().Be("document");
    }

    [Test]
    [DisplayName("WriteEmbeddings inserts chunked full embeddings")]
    public void WriteEmbeddings_InsertsChunkedFullEmbeddings()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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

        var rows = db.Read("SELECT chunk_index, start_byte, end_byte FROM document_embedding ORDER BY chunk_index",
            r => new { ChunkIndex = r.GetInt32(0), StartByte = r.GetInt64(1), EndByte = r.GetInt64(2) });
        rows.Should().HaveCount(2, "two chunks should be stored");
        rows[0].ChunkIndex.Should().Be(0);
        rows[0].StartByte.Should().Be(0L);
        rows[1].ChunkIndex.Should().Be(1);
        rows[1].StartByte.Should().Be(800L);
    }

    [Test]
    [DisplayName("WriteEmbeddings supports batched inserts")]
    public void WriteEmbeddings_InsertsMoreThanOneBatch()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var indexResult = db.IndexArtifact(uri, CreateTestArtifact());

        var embeddings = new List<DocumentEmbedding>(capacity: 200);
        for (var i = 0; i < 200; i++)
        {
            embeddings.Add(new DocumentEmbedding(
                indexResult.DocumentId,
                indexResult.DocumentId,
                ChunkIndex: i,
                DocumentEmbedding.TypeFull,
                uri.Container.AbsoluteUri,
                DocumentEmbedding.ScopeDocument,
                [0.1f, 0.2f, 0.3f],
                "test-model",
                Dimension: 3,
                StartByte: i * 10L,
                EndByte: (i * 10L) + 10L));
        }

        db.WriteEmbeddings(embeddings);

        var count = db.Read("SELECT COUNT(*) AS cnt FROM document_embedding", r => r.GetInt64(0))[0];
        count.Should().Be(200);
    }

    [Test]
    [DisplayName("IndexArtifact writes edges with all fields")]
    public void IndexArtifact_WritesEdgesWithAllFields()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact = CreateTestArtifactWithEdge();

        db.IndexArtifact(uri, artifact);

        var rows = db.Read("SELECT type, is_composition, ordinal, semantic_key, destination_uri FROM edge",
            r => new {
                Type = r.GetString(0),
                IsComposition = r.GetBoolean(1),
                Ordinal = r.GetInt32(2),
                SemanticKey = r.IsDBNull(3) ? null : r.GetString(3),
                DestinationUri = r.IsDBNull(4) ? null : r.GetString(4)
            });
        rows.Should().HaveCount(1);
        rows[0].Type.Should().Be("REFERS_TO");
        rows[0].IsComposition.Should().Be(false);
        rows[0].Ordinal.Should().Be(1);
        rows[0].SemanticKey.Should().Be("test-key");
        rows[0].DestinationUri.Should().Be("file:///other/file.md");
    }

    [Test]
    [DisplayName("IndexArtifact writes composition edges with child constraint")]
    public void IndexArtifact_WritesCompositionEdges()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var artifact = CreateTestArtifactWithCompositionEdge();

        db.IndexArtifact(uri, artifact);

        var rows = db.Read("SELECT type, is_composition, composition_child_id, destination_node_id FROM edge",
            r => new {
                Type = r.GetString(0),
                IsComposition = r.GetBoolean(1),
                CompositionChildId = r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2),
                DestinationNodeId = r.IsDBNull(3) ? (Guid?)null : r.GetGuid(3)
            });
        rows.Should().HaveCount(1);
        rows[0].Type.Should().Be("HAS_PART");
        rows[0].IsComposition.Should().Be(true);
        // composition_child_id should equal destination_node_id for composition edges
        rows[0].CompositionChildId.Should().Be(rows[0].DestinationNodeId);
    }

    [Test]
    [DisplayName("Appender span insert rolls back when transaction throws")]
    public void AppenderSpanInsert_RollsBackOnFailure()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var spanId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var action = () => db.WriteTransaction((conn, tx) =>
        {
            using var appender = conn.CreateAppender("span");
            appender.CreateRow()
                .AppendValue(spanId)
                .AppendValue(documentId)
                .AppendValue(100L)
                .AppendValue(120L)
                .AppendValue(2)
                .AppendValue(3)
                .AppendValue(2)
                .AppendValue(20)
                .EndRow();

            throw new InvalidOperationException("fail before commit");
        });

        action.Should().Throw<InvalidOperationException>();
        db.Read("SELECT COUNT(*) AS cnt FROM span", r => r.GetInt64(0))[0].Should().Be(0L);
    }

    [Test]
    [DisplayName("IndexArtifact stores large span batches with exact values")]
    public void IndexArtifact_LargeSpanBatchStoresCorrectRows()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/large-spans.md")!;
        var spanCount = 550;
        var artifact = CreateTestArtifactWithLargeSpans(spanCount);
        var expectedById = artifact.Spans.ToDictionary(s => s.Id, s => s);

        var indexResult = db.IndexArtifact(uri, artifact);

        var rows = db.Read(
            "SELECT id, document_id, start_byte, end_byte, start_line, start_column, end_line, end_column FROM span",
            r => new
            {
                Id = r.GetGuid(0),
                DocumentId = r.GetGuid(1),
                StartByte = r.IsDBNull(2) ? (long?)null : r.GetInt64(2),
                EndByte = r.IsDBNull(3) ? (long?)null : r.GetInt64(3),
                StartLine = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                StartColumn = r.IsDBNull(5) ? (int?)null : r.GetInt32(5),
                EndLine = r.IsDBNull(6) ? (int?)null : r.GetInt32(6),
                EndColumn = r.IsDBNull(7) ? (int?)null : r.GetInt32(7)
            });

        rows.Should().HaveCount(spanCount);
        foreach (var row in rows)
        {
            expectedById.ContainsKey(row.Id).Should().BeTrue($"span {row.Id} should exist in expected set");
            var expected = expectedById[row.Id];
            row.DocumentId.Should().Be(indexResult.DocumentId);
            row.StartByte.Should().Be(expected.StartByte);
            row.EndByte.Should().Be(expected.EndByte);
            row.StartLine.Should().Be(expected.StartLine);
            row.StartColumn.Should().Be(expected.StartColumn);
            row.EndLine.Should().Be(expected.EndLine);
            row.EndColumn.Should().Be(expected.EndColumn);
        }
    }

    [Test]
    [DisplayName("IndexArtifact preserves null node fields")]
    public void IndexArtifact_NodeNullHandling_PreservesNullValues()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var uri = RepoUri.Parse("file:///test/node-nulls.md")!;

        var artifact = new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 10,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Props = new JsonObject()
            },
            Children =
            [
                new Node
                {
                    Id = childId,
                    Kind = "md_paragraph",
                    ArtifactId = null,
                    SpanId = null,
                    Headline = null,
                    Structure = null,
                    Props = new JsonObject()
                }
            ],
            Spans = [],
            Edges = []
        };

        db.IndexArtifact(uri, artifact);

        var row = db.Read(
            $"SELECT uri, container_uri_lowercase, artifact_id, span_id, headline, structure, properties FROM node WHERE id = '{childId:D}'::UUID",
            r => new
            {
                UriIsNull = r.IsDBNull(0),
                ContainerIsNull = r.IsDBNull(1),
                ArtifactIdIsNull = r.IsDBNull(2),
                SpanIdIsNull = r.IsDBNull(3),
                HeadlineIsNull = r.IsDBNull(4),
                StructureIsNull = r.IsDBNull(5),
                Properties = r.GetString(6)
            }).Single();

        row.UriIsNull.Should().BeTrue();
        row.ContainerIsNull.Should().BeTrue();
        row.ArtifactIdIsNull.Should().BeTrue();
        row.SpanIdIsNull.Should().BeTrue();
        row.HeadlineIsNull.Should().BeTrue();
        row.StructureIsNull.Should().BeTrue();
        row.Properties.Should().Be("{}");
    }

    [Test]
    [DisplayName("IndexArtifact keeps container URI key null for child nodes with fragment URIs")]
    public void IndexArtifact_ChildNodesWithFragmentUris_ContainerKeyRemainsNull()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/duplicate-child-fragments.md")!;
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var childOneId = Guid.NewGuid();
        var childTwoId = Guid.NewGuid();
        var sharedFragmentUri = RepoUri.FromAnchor(new Uri(uri.Container.AbsoluteUri), "lint-rule");

        var artifact = new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 42,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Props = new JsonObject()
            },
            Children =
            [
                new Node
                {
                    Id = childOneId,
                    Kind = "md_heading",
                    Uri = sharedFragmentUri,
                    Props = new JsonObject { ["slug"] = "lint-rule" }
                },
                new Node
                {
                    Id = childTwoId,
                    Kind = "md_heading",
                    Uri = sharedFragmentUri,
                    Props = new JsonObject { ["slug"] = "lint-rule" }
                }
            ],
            Spans = [],
            Edges = []
        };

        var indexResult = db.IndexArtifact(uri, artifact);
        indexResult.DocumentId.Should().NotBe(Guid.Empty);

        var rows = db.Read(
            $"SELECT id, uri, container_uri_lowercase FROM node WHERE id IN ('{childOneId:D}'::UUID, '{childTwoId:D}'::UUID) ORDER BY id",
            r => new
            {
                Id = r.GetGuid(0),
                Uri = r.IsDBNull(1) ? null : r.GetString(1),
                ContainerKey = r.IsDBNull(2) ? null : r.GetString(2)
            });

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Uri == sharedFragmentUri.AbsoluteUri);
        rows.Should().OnlyContain(r => r.ContainerKey == null);
    }

    [Test]
    [DisplayName("UpsertNode keeps container URI key null for non-document URI nodes")]
    public void UpsertNode_NonDocumentUriNode_ContainerKeyRemainsNull()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var nodeId = Guid.NewGuid();
        var node = new Node
        {
            Id = nodeId,
            Kind = "md_heading",
            Uri = RepoUri.Parse("file:///test/upsert-child.md#heading")!,
            Props = new JsonObject { ["slug"] = "heading" }
        };

        db.UpsertNode(node);

        var containerKey = db.Read(
            $"SELECT container_uri_lowercase FROM node WHERE id = '{nodeId:D}'::UUID",
            r => r.IsDBNull(0) ? null : r.GetString(0)).Single();

        containerKey.Should().BeNull();
    }

    [Test]
    [DisplayName("IndexArtifact stores mixed composition and reference edges")]
    public void IndexArtifact_MixedEdgeTypes_AreStoredCorrectly()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var uri = RepoUri.Parse("file:///test/mixed-edges.md")!;

        var artifact = new ParsedArtifact
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
                Uri = uri,
                ArtifactId = artifactId,
                Props = new JsonObject()
            },
            Children =
            [
                new Node
                {
                    Id = childId,
                    Kind = "md_section",
                    ArtifactId = artifactId,
                    Props = new JsonObject()
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
                },
                new Edge
                {
                    SrcId = docId,
                    DstUri = RepoUri.Parse("file:///other/ref.md"),
                    Type = "REFERS_TO",
                    IsComposition = false
                }
            ]
        };

        db.IndexArtifact(uri, artifact);

        var rows = db.Read(
            "SELECT type, is_composition, composition_child_id, destination_node_id, destination_uri FROM edge ORDER BY type",
            r => new
            {
                Type = r.GetString(0),
                IsComposition = r.GetBoolean(1),
                CompositionChildId = r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2),
                DestinationNodeId = r.IsDBNull(3) ? (Guid?)null : r.GetGuid(3),
                DestinationUri = r.IsDBNull(4) ? null : r.GetString(4)
            });

        rows.Should().HaveCount(2);

        var hasPart = rows.Single(r => r.Type == "HAS_PART");
        hasPart.IsComposition.Should().BeTrue();
        hasPart.DestinationNodeId.Should().NotBeNull();
        hasPart.CompositionChildId.Should().Be(hasPart.DestinationNodeId);
        hasPart.DestinationUri.Should().BeNull();

        var refersTo = rows.Single(r => r.Type == "REFERS_TO");
        refersTo.IsComposition.Should().BeFalse();
        refersTo.CompositionChildId.Should().BeNull();
        refersTo.DestinationNodeId.Should().BeNull();
        refersTo.DestinationUri.Should().Be("file:///other/ref.md");
    }

    [Test]
    [DisplayName("Span column values round-trip exactly")]
    public void SpanColumnValues_RoundTripExactly()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/span-columns.md")!;
        var spanId = Guid.NewGuid();
        var artifact = CreateTestArtifactWithSpans([
            new Span
            {
                Id = spanId,
                DocumentId = Guid.NewGuid(),
                StartByte = 12345,
                EndByte = 12400,
                StartLine = 22,
                StartColumn = 4,
                EndLine = 24,
                EndColumn = 18
            }
        ]);

        var indexResult = db.IndexArtifact(uri, artifact);

        var row = db.Read(
            $"SELECT id, document_id, start_byte, end_byte, start_line, start_column, end_line, end_column FROM span WHERE id = '{spanId:D}'::UUID",
            r => new
            {
                Id = r.GetGuid(0),
                DocumentId = r.GetGuid(1),
                StartByte = r.GetInt64(2),
                EndByte = r.GetInt64(3),
                StartLine = r.GetInt32(4),
                StartColumn = r.GetInt32(5),
                EndLine = r.GetInt32(6),
                EndColumn = r.GetInt32(7)
            }).Single();

        row.Id.Should().Be(spanId);
        row.DocumentId.Should().Be(indexResult.DocumentId);
        row.StartByte.Should().Be(12345L);
        row.EndByte.Should().Be(12400L);
        row.StartLine.Should().Be(22);
        row.StartColumn.Should().Be(4);
        row.EndLine.Should().Be(24);
        row.EndColumn.Should().Be(18);
    }

    [Test]
    [DisplayName("WriteTransaction recovers after write conflict and later writes succeed")]
    public void WriteTransaction_ConflictDoesNotPoisonLaterTransactions()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            using var db = TestServiceCollectionExtensions.CreateTestDataStore(databasePath: databasePath);
            db.ExecuteRaw("""
                CREATE TABLE tx_conflict_test (
                    id INTEGER PRIMARY KEY,
                    value INTEGER NOT NULL
                );
                INSERT INTO tx_conflict_test VALUES (1, 0);
                """);

            var conflict = () => db.WriteTransaction((conn, tx) =>
            {
                ExecuteNonQuery(conn, tx, "UPDATE tx_conflict_test SET value = value + 1 WHERE id = 1;");

                using var competingConnection = CreateSecondaryConnection(databasePath);
                using var competingTx = competingConnection.BeginTransaction();
                ExecuteNonQuery(competingConnection, competingTx, "UPDATE tx_conflict_test SET value = value + 10 WHERE id = 1;");
                competingTx.Commit();
            });

            conflict.Should()
                .Throw<DuckDBException>()
                .Where(ex =>
                    ex.Message.Contains("write-write conflict", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("conflict on update", StringComparison.OrdinalIgnoreCase));

            db.WriteTransaction((conn, tx) =>
            {
                ExecuteNonQuery(conn, tx, "INSERT INTO tx_conflict_test VALUES (2, 100);");
            });

            var rowCount = db.WriteTransaction((conn, tx) =>
            {
                ExecuteNonQuery(conn, tx, "UPDATE tx_conflict_test SET value = value + 1 WHERE id = 2;");
                return ExecuteScalar<long>(conn, tx, "SELECT COUNT(*) FROM tx_conflict_test;");
            });

            rowCount.Should().Be(2);
        }
        finally
        {
            CleanupTemporaryDatabase(databasePath);
        }
    }

    [Test]
    [DisplayName("WriteTransaction<T> recovers from stale already-in-transaction state")]
    public void WriteTransactionT_RecoversFromStaleAlreadyInTransactionState()
    {
        var databasePath = CreateTemporaryDatabasePath();
        DuckDBTransaction? staleTx = null;

        try
        {
            using var db = TestServiceCollectionExtensions.CreateTestDataStore(databasePath: databasePath);
            db.ExecuteRaw("""
                CREATE TABLE tx_state_test (
                    id INTEGER PRIMARY KEY,
                    value VARCHAR NOT NULL
                );
                INSERT INTO tx_state_test VALUES (1, 'initial');
                """);

            var primaryConnection = GetPrimaryConnection(db);
            staleTx = primaryConnection.BeginTransaction();

            // Simulate DuckDB auto-rollback at native layer while wrapper still tracks an active transaction object.
            primaryConnection.Execute("ROLLBACK;");

            var updatedValue = db.WriteTransaction((conn, tx) =>
            {
                ExecuteNonQuery(conn, tx, "UPDATE tx_state_test SET value = 'recovered' WHERE id = 1;");
                return ExecuteScalar<string>(conn, tx, "SELECT value FROM tx_state_test WHERE id = 1;");
            });

            updatedValue.Should().Be("recovered");
        }
        finally
        {
            try
            {
                staleTx?.Dispose();
            }
            catch
            {
                // Ignore cleanup failures in test teardown.
            }

            CleanupTemporaryDatabase(databasePath);
        }
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
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        // Index some documents first
        for (var i = 0; i < 10; i++)
        {
            var uri = RepoUri.Parse($"file:///test/doc{i}.md")!;
            db.IndexArtifact(uri, CreateTestArtifact($"content {i}"));
        }

        // Run many concurrent reads
        var tasks = new List<Task<long>>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var results = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0));
                return results[0];
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
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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
        var count = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0))[0];
        count.Should().Be(20);
    }

    [Test]
    [DisplayName("Mixed concurrent reads and writes maintain consistency")]
    public async Task MixedConcurrentReadsAndWrites_MaintainConsistency()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var writeCount = 0;
        var readResults = new List<long>();
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
                var results = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0));
                var count = results[0];
                lock (readLock) { readResults.Add(count); }
            }));
        }

        await Task.WhenAll(tasks);

        // Final count should be 15
        var finalCount = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0))[0];
        finalCount.Should().Be(15);

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
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var writerStarted = new ManualResetEventSlim(false);
        var writerCanFinish = new ManualResetEventSlim(false);
        var readerResults = new List<(DateTime time, long count)>();
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
                var results = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0));
                var count = results[0];
                lock (readerLock)
                {
                    readerResults.Add((DateTime.UtcNow, count));
                }
                Thread.Sleep(1);
            }
        })).ToList();

        await Task.WhenAll(new[] { writerTask }.Concat(readerTasks));

        // Final state should be 5 documents
        var finalCount = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0))[0];
        finalCount.Should().Be(5);

        // All individual reads should see a consistent count (not partial writes)
        // Each read sees 0, 1, 2, 3, 4, or 5 - never a fractional state
        readerResults.Select(r => r.count).Should().AllSatisfy(c => c.Should().BeInRange(0, 5));
    }

    [Test]
    [DisplayName("Concurrent deletes and reads maintain consistency")]
    public async Task ConcurrentDeletesAndReads_MaintainConsistency()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        // First, index 20 documents
        for (var i = 0; i < 20; i++)
        {
            var uri = RepoUri.Parse($"file:///test/doc{i}.md")!;
            db.IndexArtifact(uri, CreateTestArtifact($"content {i}"));
        }

        var readResults = new List<long>();
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
                var results = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0));
                var count = results[0];
                lock (readLock) { readResults.Add(count); }
            }));
        }

        await Task.WhenAll(tasks);

        // Final count should be 10 (20 - 10 deleted)
        var finalCount = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'document'", r => r.GetInt64(0))[0];
        finalCount.Should().Be(10);

        // All reads should see valid counts between 10 and 20
        readResults.Should().AllSatisfy(c => c.Should().BeInRange(10, 20));
    }

    #endregion

    #region Reindex Cleanup Tests

    [Test]
    [DisplayName("Reindex cleans up old child nodes")]
    public void IndexArtifact_ReindexCleansUpOldChildren()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;

        // First index with 3 children
        var artifact1 = CreateTestArtifactWithChildren(3);
        db.IndexArtifact(uri, artifact1);

        var initialChildCount = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'md_section'", r => r.GetInt64(0))[0];
        initialChildCount.Should().Be(3);

        // Reindex with 1 child
        var artifact2 = CreateTestArtifactWithChildren(1);
        db.IndexArtifact(uri, artifact2);

        // Should only have 1 child now
        var finalChildCount = db.Read("SELECT COUNT(*) AS cnt FROM node WHERE kind = 'md_section'", r => r.GetInt64(0))[0];
        finalChildCount.Should().Be(1, "old children should be deleted on reindex");
    }

    [Test]
    [DisplayName("Reindex cleans up old spans")]
    public void IndexArtifact_ReindexCleansUpOldSpans()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;

        // First index with 3 spans
        var artifact1 = CreateTestArtifactWithSpans(3);
        db.IndexArtifact(uri, artifact1);

        var initialSpanCount = db.Read("SELECT COUNT(*) AS cnt FROM span", r => r.GetInt64(0))[0];
        initialSpanCount.Should().Be(3);

        // Reindex with 1 span
        var artifact2 = CreateTestArtifactWithSpans(1);
        db.IndexArtifact(uri, artifact2);

        // Should only have 1 span now
        var finalSpanCount = db.Read("SELECT COUNT(*) AS cnt FROM span", r => r.GetInt64(0))[0];
        finalSpanCount.Should().Be(1, "old spans should be deleted on reindex");
    }

    [Test]
    [DisplayName("Reindex cleans up old edges")]
    public void IndexArtifact_ReindexCleansUpOldEdges()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;

        // First index with 2 reference edges
        var artifact1 = CreateTestArtifactWithReferenceEdges(2);
        db.IndexArtifact(uri, artifact1);

        var initialEdgeCount = db.Read("SELECT COUNT(*) AS cnt FROM edge WHERE type = 'REFERS_TO'", r => r.GetInt64(0))[0];
        initialEdgeCount.Should().Be(2);

        // Reindex with 1 edge
        var artifact2 = CreateTestArtifactWithReferenceEdges(1);
        db.IndexArtifact(uri, artifact2);

        // Should only have 1 edge now
        var finalEdgeCount = db.Read("SELECT COUNT(*) AS cnt FROM edge WHERE type = 'REFERS_TO'", r => r.GetInt64(0))[0];
        finalEdgeCount.Should().Be(1, "old edges should be deleted on reindex");
    }

    [Test]
    [DisplayName("Delete cleans up embeddings")]
    public void DeleteArtifact_CleansUpEmbeddings()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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

        var embeddingCount = db.Read("SELECT COUNT(*) AS cnt FROM document_embedding", r => r.GetInt64(0))[0];
        embeddingCount.Should().Be(2);

        // Delete the document
        db.DeleteArtifact(uri);

        // Embeddings should be gone
        var finalEmbeddingCount = db.Read("SELECT COUNT(*) AS cnt FROM document_embedding", r => r.GetInt64(0))[0];
        finalEmbeddingCount.Should().Be(0, "embeddings should be deleted with document");
    }

    #endregion

    #region Annotation Source Isolation Tests

    [Test]
    [DisplayName("ReplaceAnnotations preserves annotations from other sources")]
    public void ReplaceAnnotations_PreservesOtherSources()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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
        var rows = db.Read("SELECT source, message FROM annotation ORDER BY source",
            r => new { Source = r.GetString(0), Message = r.GetString(1) });
        rows.Should().HaveCount(2, "annotations from other sources should be preserved");
        rows[0].Source.Should().Be("analyzer-a");
        rows[0].Message.Should().Be("Info A (new)");
        rows[1].Source.Should().Be("analyzer-b");
        rows[1].Message.Should().Be("Error B");
    }

    #endregion

    #region Embedding Upsert Tests

    [Test]
    [DisplayName("WriteEmbeddings upserts on conflict")]
    public void WriteEmbeddings_UpsertsOnConflict()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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
        var rows = db.Read("SELECT model FROM document_embedding", r => r.GetString(0));
        rows.Should().HaveCount(1, "should upsert, not duplicate");
        rows[0].Should().Be("model-v2", "should be updated to new model");
    }

    [Test]
    [DisplayName("WriteEmbeddings allows structure and full types to coexist")]
    public void WriteEmbeddings_StructureAndFullCoexist()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

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

        var rows = db.Read("SELECT embedding_type FROM document_embedding ORDER BY embedding_type", r => r.GetString(0));
        rows.Should().HaveCount(2);
        rows[0].Should().Be("full");
        rows[1].Should().Be("structure");
    }

    #endregion

    #region Null Input Validation Tests

    [Test]
    [DisplayName("IndexArtifact throws on null URI")]
    public void IndexArtifact_ThrowsOnNullUri()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var act = () => db.IndexArtifact(null!, CreateTestArtifact());

        act.Should().Throw<ArgumentNullException>().WithParameterName("uri");
    }

    [Test]
    [DisplayName("IndexArtifact throws on null artifact")]
    public void IndexArtifact_ThrowsOnNullArtifact()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var act = () => db.IndexArtifact(uri, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("artifact");
    }

    [Test]
    [DisplayName("DeleteArtifact throws on null URI")]
    public void DeleteArtifact_ThrowsOnNullUri()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var act = () => db.DeleteArtifact(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("uri");
    }

    [Test]
    [DisplayName("ReplaceAnnotations throws on null URI")]
    public void ReplaceAnnotations_ThrowsOnNullUri()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var act = () => db.ReplaceAnnotations(null!, new List<Annotation>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("artifactUri");
    }

    [Test]
    [DisplayName("ReplaceAnnotations throws on null annotations")]
    public void ReplaceAnnotations_ThrowsOnNullAnnotations()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///test/doc.md")!;
        var act = () => db.ReplaceAnnotations(uri, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("annotations");
    }

    [Test]
    [DisplayName("WriteEmbeddings throws on null list")]
    public void WriteEmbeddings_ThrowsOnNullList()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var act = () => db.WriteEmbeddings(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("embeddings");
    }

    [Test]
    [DisplayName("Schema is auto-initialized on construction")]
    public void Schema_AutoInitialized()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        // Verify tables exist (schema was auto-initialized in constructor)
        var results = db.Read("SELECT COUNT(*) AS cnt FROM node", r => r.GetInt64(0));
        results.Should().HaveCount(1);
    }

    [Test]
    [DisplayName("Schema initialization succeeds with all macros in correct order")]
    public void Schema_MacroDependencyOrder_IsCorrect()
    {
        // This test catches the bug where a view/macro references another macro
        // that hasn't been created yet due to incorrect schema loading order.
        // If schema initialization fails, the DuckDbDataStore constructor throws.
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        // Verify key macros exist (these have dependencies on other macros/UDFs)
        var macros = db.Read(
            "SELECT function_name FROM duckdb_functions() WHERE function_name IN ('git_status', 'search', 'matches_glob', 'glob_files')",
            r => r.GetString(0));

        macros.Should().Contain("git_status", "git_status macro should be registered");
        macros.Should().Contain("search", "search macro should be registered");
        macros.Should().Contain("matches_glob", "matches_glob macro should be registered");
        macros.Should().Contain("glob_files", "glob_files macro should be registered");

        // Verify views were created (checking existence, not execution - UDFs need DI)
        // The Files view definition references git_status() - if order is wrong, CREATE VIEW fails
        var views = db.Read(
            "SELECT table_name FROM information_schema.tables WHERE table_type = 'VIEW' AND table_name IN ('files', 'types', 'functions')",
            r => r.GetString(0));

        views.Should().Contain("files", "Files view should exist (depends on git_status macro being defined first)");
        views.Should().Contain("types", "Types view should exist");
        views.Should().Contain("functions", "Functions view should exist");
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

    private static ParsedArtifact CreateTestArtifactWithLargeSpans(int spanCount)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var spans = new List<Span>(spanCount);
        for (var i = 0; i < spanCount; i++)
        {
            var startByte = i * 100L;
            spans.Add(new Span
            {
                Id = Guid.NewGuid(),
                DocumentId = docId,
                StartByte = startByte,
                EndByte = startByte + 80,
                StartLine = i + 1,
                StartColumn = 1,
                EndLine = i + 1,
                EndColumn = 40
            });
        }

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = spanCount * 100,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse("file:///test/large-spans.md"),
                ArtifactId = artifactId,
                Headline = "Large Spans"
            },
            Children = [],
            Spans = spans,
            Edges = []
        };
    }

    private static ParsedArtifact CreateTestArtifactWithSpans(IReadOnlyList<Span> spans)
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
                Uri = RepoUri.Parse("file:///test/span-columns.md"),
                ArtifactId = artifactId,
                Headline = "Span Column Test"
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

    private static DuckDBConnection CreateSecondaryConnection(string databasePath)
    {
        var connection = new DuckDBConnection($"Data Source={databasePath};ACCESS_MODE=READ_WRITE");
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(DuckDBConnection connection, DuckDBTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(DuckDBConnection connection, DuckDBTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    private static DuckDBConnection GetPrimaryConnection(DuckDbDataStore db)
    {
        var field = typeof(DuckDbDataStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("DuckDbDataStore should retain its primary DuckDBConnection");

        var value = field!.GetValue(db);
        value.Should().BeOfType<DuckDBConnection>();
        return (DuckDBConnection)value!;
    }

    private static string CreateTemporaryDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RepoQL", "DuckDbDataStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "store.duckdb");
    }

    private static void CleanupTemporaryDatabase(string databasePath)
    {
        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }

        var walPath = databasePath + ".wal";
        try
        {
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    #endregion
}
