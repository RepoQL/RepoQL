using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

public class AnnotationsTests
{
    [Test]
    public void UpsertAnnotation_Idempotent_BySemanticKey()
    {
        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: true);
        store.EnsureSchema();

        // Seed document
        var uri = RepoUri.Parse("file:///repo/doc.md");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        var a1 = new Annotation
        {
            SemanticKey = "lint:MD001:line1",
            Kind = "lint",
            Severity = "warning",
            Source = "markdownlint",
            RuleId = "MD001",
            Message = "Heading levels should only increment by one level at a time",
            Data = new JsonObject { ["line"] = 1 },
            ScopeDocumentId = doc.Id
        };
        var saved1 = store.UpsertAnnotation(a1);

        // Upsert same semantic key with updated message
        var a2 = new Annotation
        {
            Id = saved1.Id,
            SemanticKey = saved1.SemanticKey,
            Kind = saved1.Kind,
            Severity = saved1.Severity,
            Source = saved1.Source,
            RuleId = saved1.RuleId,
            Message = "Updated message",
            Data = saved1.Data,
            ScopeDocumentId = saved1.ScopeDocumentId,
            TargetNodeId = saved1.TargetNodeId,
            TargetEdgeId = saved1.TargetEdgeId,
            TargetSpanId = saved1.TargetSpanId,
            TargetUri = saved1.TargetUri,
            CreatedAt = saved1.CreatedAt,
            ExpiresAt = saved1.ExpiresAt
        };
        var saved2 = store.UpsertAnnotation(a2);

        saved2.Id.Should().Be(saved1.Id);
        var roundTrip = store.GetAnnotation(saved1.Id);
        roundTrip!.Message.Should().Be("Updated message");
    }

    [Test]
    public void GetAnnotationsForDocument_FiltersByKindsAndSeverity()
    {
        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: true);
        store.EnsureSchema();

        var uri = RepoUri.Parse("file:///repo/a.cs");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        // Seed various annotations
        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "lint:CS0001",
            Kind = "lint",
            Severity = "info",
            Source = "cs-lint",
            Message = "style suggestion",
            ScopeDocumentId = doc.Id,
        });
        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "lint:CS0002",
            Kind = "lint",
            Severity = "warning",
            Source = "cs-lint",
            Message = "possible issue",
            ScopeDocumentId = doc.Id,
        });
        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "outline:1",
            Kind = "outline",
            Severity = "hint",
            Source = "parser",
            Message = "outline item",
            ScopeDocumentId = doc.Id,
        });

        // kinds=lint, min_severity=warning -> should return only CS0002
        var filtered = store.GetAnnotationsForDocument(doc.Id, kinds: "lint", minSeverity: "warning").ToArray();
        filtered.Length.Should().Be(1);
        filtered[0].Severity.ToLowerInvariant().Should().Be("warning");
    }

    [Test]
    public void AnnotationsMacros_WorkViaRawQuery()
    {
        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: true);
        store.EnsureSchema();

        var uri = RepoUri.Parse("file:///repo/b.md");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });
        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "lint:MD:1",
            Kind = "lint",
            Severity = "error",
            Source = "md-lint",
            Message = "bad",
            ScopeDocumentId = doc.Id
        });

        var rows = store.RawQuery("SELECT kind,severity FROM annotations_for(?, 'lint', 'info')", uri.AbsoluteUri).ToArray();
        rows.Length.Should().Be(1);
        rows[0]["kind"]!.ToString()!.ToLowerInvariant().Should().Be("lint");
        rows[0]["severity"]!.ToString()!.ToLowerInvariant().Should().Be("error");
    }

    [Test]
    public void SnippetMacro_WorksViaRawQuery()
    {
        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: true);
        store.EnsureSchema();

        // Seed artifact with text and document
        var art = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "xxh64:1111111111111111",
            Size = 20,
            MediaType = SemanticMediaType.Create("text", "markdown"),
            Text = "First line\nSecond line\nThird line\n"
        };
        store.UpsertArtifact(art);

        var uri = RepoUri.Parse("file:///repo/snippet.md");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, ArtifactId = art.Id, Props = new JsonObject() });

        // Focus on line 2
        var rows = store.RawQuery("SELECT line_number, text, is_focus FROM snippet(?, 1);", uri.AbsoluteUri + "#line=2").ToArray();
        rows.Length.Should().BeGreaterThan(0);
        rows.Any(r => Convert.ToInt32(r["line_number"]) == 2 && (bool)r["is_focus"]!).Should().BeTrue();
    }
}