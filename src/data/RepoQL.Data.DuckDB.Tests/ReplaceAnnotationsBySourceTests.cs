using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB.Tests;

public class ReplaceAnnotationsBySourceTests
{
    [Test]
    [DisplayName("ReplaceAnnotationsBySource inserts new annotations when source has none")]
    public void ReplaceAnnotationsBySource_InsertsNewAnnotations()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-insert.md");

        var result = db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [CreateAnnotation("key-1", docId, "First finding")],
            []);

        result.Inserted.Should().Be(1);
        result.Updated.Should().Be(0);
        result.Expired.Should().Be(0);

        CountAnnotations(db, "scanner-a", "lint").Should().Be(1L);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource deletes stale semantic keys for source and kind")]
    public void ReplaceAnnotationsBySource_DeletesStaleAnnotations()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-stale.md");

        db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [
                CreateAnnotation("key-a", docId, "A"),
                CreateAnnotation("key-b", docId, "B")
            ],
            []);

        var result = db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [CreateAnnotation("key-b", docId, "B")],
            []);

        result.Inserted.Should().Be(0);
        result.Updated.Should().Be(0);
        result.Expired.Should().Be(1);

        var keys = db.Read(
            "SELECT semantic_key FROM annotation WHERE source = 'scanner-a' AND kind = 'lint'",
            r => r.GetString(0));
        keys.Should().BeEquivalentTo(["key-b"]);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource does not duplicate unchanged annotations")]
    public void ReplaceAnnotationsBySource_UnchangedAnnotations_AreNotDuplicated()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-unchanged.md");

        db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [CreateAnnotation("key-stable", docId, "Stable message")],
            []);

        var result = db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [CreateAnnotation("key-stable", docId, "Stable message")],
            []);

        result.Inserted.Should().Be(0);
        result.Updated.Should().Be(0);
        result.Expired.Should().Be(0);
        CountAnnotations(db, "scanner-a", "lint").Should().Be(1L);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource is idempotent for identical input")]
    public void ReplaceAnnotationsBySource_IdempotentRewrite_SecondPassIsNoOp()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-idempotent.md");
        var annotations = new List<Annotation>
        {
            CreateAnnotation("key-1", docId, "One"),
            CreateAnnotation("key-2", docId, "Two")
        };

        db.ReplaceAnnotationsBySource("scanner-a", "lint", annotations, []);
        var second = db.ReplaceAnnotationsBySource("scanner-a", "lint", annotations, []);

        second.Inserted.Should().Be(0);
        second.Updated.Should().Be(0);
        second.Expired.Should().Be(0);
        CountAnnotations(db, "scanner-a", "lint").Should().Be(2L);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource updates annotation when content changes for same key")]
    public void ReplaceAnnotationsBySource_ContentChange_CountsAsUpdated()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-update.md");

        db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [CreateAnnotation("key-1", docId, "Original message")],
            []);

        var result = db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [CreateAnnotation("key-1", docId, "Revised message")],
            []);

        result.Inserted.Should().Be(0);
        result.Updated.Should().Be(1);
        result.Expired.Should().Be(0);
        CountAnnotations(db, "scanner-a", "lint").Should().Be(1L);

        var messages = db.Read(
            "SELECT message FROM annotation WHERE source = 'scanner-a' AND kind = 'lint'",
            r => r.GetString(0));
        messages.Should().BeEquivalentTo(["Revised message"]);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource empty set expires all annotations for source and kind")]
    public void ReplaceAnnotationsBySource_EmptyInput_DeletesAllForSourceAndKind()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-empty.md");

        db.ReplaceAnnotationsBySource(
            "scanner-a",
            "lint",
            [
                CreateAnnotation("key-1", docId, "One"),
                CreateAnnotation("key-2", docId, "Two")
            ],
            []);

        var result = db.ReplaceAnnotationsBySource("scanner-a", "lint", [], []);

        result.Inserted.Should().Be(0);
        result.Updated.Should().Be(0);
        result.Expired.Should().Be(2);
        CountAnnotations(db, "scanner-a", "lint").Should().Be(0L);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource does not touch annotations from other sources")]
    public void ReplaceAnnotationsBySource_PreservesDifferentSource()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-isolation.md");

        db.ReplaceAnnotationsBySource("scanner-a", "lint", [CreateAnnotation("key-a", docId, "A")], []);
        db.ReplaceAnnotationsBySource("scanner-b", "lint", [CreateAnnotation("key-b", docId, "B")], []);

        db.ReplaceAnnotationsBySource("scanner-a", "lint", [CreateAnnotation("key-a2", docId, "A2")], []);

        CountAnnotations(db, "scanner-a", "lint").Should().Be(1L);
        CountAnnotations(db, "scanner-b", "lint").Should().Be(1L);

        var scannerBMessages = db.Read(
            "SELECT message FROM annotation WHERE source = 'scanner-b' AND kind = 'lint'",
            r => r.GetString(0));
        scannerBMessages.Should().BeEquivalentTo(["B"]);
    }

    [Test]
    [DisplayName("ReplaceAnnotationsBySource supports large semantic-key sets")]
    public void ReplaceAnnotationsBySource_LargeSemanticKeySet_WritesAndExpires()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        var docId = IndexTestDocument(db, "file:///test/source-large.md");

        var large = Enumerable.Range(0, 1001)
            .Select(i => CreateAnnotation($"key-{i:D4}", docId, $"Message {i}"))
            .ToList();

        var first = db.ReplaceAnnotationsBySource("scanner-a", "lint", large, []);
        first.Inserted.Should().Be(1001);

        var secondBatch = large.Take(1000).ToList();
        var second = db.ReplaceAnnotationsBySource("scanner-a", "lint", secondBatch, []);

        second.Expired.Should().Be(1);
        CountAnnotations(db, "scanner-a", "lint").Should().Be(1000L);
    }

    private static long CountAnnotations(DuckDbDataStore db, string source, string kind)
    {
        var escapedSource = source.Replace("'", "''", StringComparison.Ordinal);
        var escapedKind = kind.Replace("'", "''", StringComparison.Ordinal);
        return db.Read(
            $"SELECT COUNT(*) FROM annotation WHERE source = '{escapedSource}' AND kind = '{escapedKind}'",
            r => r.GetInt64(0))[0];
    }

    private static Guid IndexTestDocument(DuckDbDataStore db, string uriText)
    {
        var uri = RepoUri.Parse(uriText)!;
        var artifactId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var artifact = new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 128,
                MediaType = SemanticMediaType.Parse("text/markdown")
            },
            DocumentNode = new Node
            {
                Id = documentId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = "Test document"
            },
            Children = [],
            Spans = [],
            Edges = []
        };

        var result = db.IndexArtifact(uri, artifact);
        return result.DocumentId;
    }

    private static Annotation CreateAnnotation(string semanticKey, Guid documentId, string message)
    {
        return new Annotation
        {
            SemanticKey = semanticKey,
            Kind = "lint",
            Severity = "warning",
            Source = "ignored-in-replace",
            Message = message,
            Data = new JsonObject(),
            ScopeDocumentId = documentId
        };
    }
}
