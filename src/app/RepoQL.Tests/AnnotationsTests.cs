using System.Text.Json.Nodes;
using RepoQL.Contracts.Analysis;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Core.Analysis;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

internal class AnnotationsTests
{
    [Test]
    public async Task AnnotationWriter_RemovesStaleAnnotations_WhenNoResults()
    {
        using var store = new DuckDbDataStore();

        var uri = RepoUri.Parse("file:///repo/clean.cs");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        var writer = new AnnotationResultWriter(store);
        var results = new[]
        {
            new AnalysisResult
            {
                SemanticKey = "lint:CS1000",
                Kind = "lint",
                Severity = AnalysisSeverity.Warning,
                Source = "cs-lint",
                RuleId = "CS1000",
                Message = "first issue",
                Data = new JsonObject(),
                Target = new AnalysisTarget { TargetUri = uri }
            }
        };

        await writer.WriteAsync(uri.AbsoluteUri, results, ["cs-lint"], CancellationToken.None);
        store.GetAnnotationsForDocument(doc.Id).Should().HaveCount(1);

        await writer.WriteAsync(uri.AbsoluteUri, Array.Empty<AnalysisResult>(), ["cs-lint"], CancellationToken.None);
        store.GetAnnotationsForDocument(doc.Id).Should().BeEmpty();
    }

    [Test]
    public async Task AnnotationWriter_PreservesAnnotationsFromOtherSources()
    {
        using var store = new DuckDbDataStore();

        var uri = RepoUri.Parse("file:///repo/mixed.cs");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "lint:CS9999",
            Kind = "lint",
            Severity = "warning",
            Source = "cs-lint",
            Message = "outdated issue",
            ScopeDocumentId = doc.Id
        });

        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "outline:1",
            Kind = "outline",
            Severity = "hint",
            Source = "outline-generator",
            Message = "outline entry",
            ScopeDocumentId = doc.Id
        });

        var writer = new AnnotationResultWriter(store);
        await writer.WriteAsync(uri.AbsoluteUri, Array.Empty<AnalysisResult>(), ["cs-lint"], CancellationToken.None);

        var remaining = store.GetAnnotationsForDocument(doc.Id).ToArray();
        remaining.Should().HaveCount(1);
        remaining[0].Source.Should().Be("outline-generator");
    }

    [Test]
    public async Task AnnotationWriter_WithDatabaseWriter_RemovesStaleAnnotations()
    {
        using var store = new DuckDbDataStore();

        var uri = RepoUri.Parse("file:///repo/clean2.cs");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        var writer = new AnnotationResultWriter(store);
        var initial = new[]
        {
            new AnalysisResult
            {
                SemanticKey = "lint:CS2000",
                Kind = "lint",
                Severity = AnalysisSeverity.Warning,
                Source = "cs-lint",
                RuleId = "CS2000",
                Message = "issue 1",
                Data = new JsonObject(),
                Target = new AnalysisTarget { TargetUri = uri }
            }
        };

        await writer.WriteAsync(uri.AbsoluteUri, initial, ["cs-lint"], CancellationToken.None);
        store.GetAnnotationsForDocument(doc.Id).Should().HaveCount(1);

        await writer.WriteAsync(uri.AbsoluteUri, Array.Empty<AnalysisResult>(), ["cs-lint"], CancellationToken.None);
        store.GetAnnotationsForDocument(doc.Id).Should().BeEmpty();
    }

    [Test]
    public void UpsertAnnotation_Idempotent_BySemanticKey()
    {
        using var store = new DuckDbDataStore();

        var uri = RepoUri.Parse("file:///repo/a.md");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        var a1 = new Annotation
        {
            Id = Guid.NewGuid(),
            SemanticKey = "lint:MD001:1",
            Kind = "lint",
            Severity = "warning",
            Source = "md-lint",
            RuleId = "MD001",
            Message = "Heading levels should only increment by one level at a time",
            Data = new JsonObject { ["line"] = 1 },
            ScopeDocumentId = doc.Id
        };
        store.UpsertAnnotation(a1);

        // Upsert same semantic key with updated message
        var a2 = new Annotation
        {
            Id = a1.Id,
            SemanticKey = a1.SemanticKey,
            Kind = a1.Kind,
            Severity = a1.Severity,
            Source = a1.Source,
            RuleId = a1.RuleId,
            Message = "Updated message",
            Data = a1.Data,
            ScopeDocumentId = a1.ScopeDocumentId,
            TargetNodeId = a1.TargetNodeId,
            TargetEdgeId = a1.TargetEdgeId,
            TargetSpanId = a1.TargetSpanId,
            TargetUri = a1.TargetUri,
            CreatedAt = a1.CreatedAt,
            ExpiresAt = a1.ExpiresAt
        };
        store.UpsertAnnotation(a2);

        var roundTrip = store.GetAnnotation(a1.Id);
        roundTrip.Should().NotBeNull();
        roundTrip!.Message.Should().Be("Updated message");
    }

    [Test]
    public void GetAnnotationsForDocument_FiltersByKindsAndSeverity()
    {
        using var store = new DuckDbDataStore();

        var uri = RepoUri.Parse("file:///repo/filter.md");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, Props = new JsonObject() });

        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "lint:CS0001",
            Kind = "lint",
            Severity = "info",
            Source = "parser",
            Message = "info message",
            ScopeDocumentId = doc.Id,
        });
        store.UpsertAnnotation(new Annotation
        {
            SemanticKey = "lint:CS0002",
            Kind = "lint",
            Severity = "warning",
            Source = "parser",
            Message = "warning message",
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

        // kinds=lint, severity=warning -> filter with LINQ
        var filtered = store.GetAnnotationsForDocument(doc.Id).Where(a => a.Kind == "lint" && a.Severity == "warning").ToArray();
        filtered.Length.Should().Be(1);
        filtered[0].Severity.ToLowerInvariant().Should().Be("warning");
    }

    [Test]
    public void AnnotationsMacros_WorkViaRawQuery()
    {
        using var store = new DuckDbDataStore();

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

        var rows = store.Query($"SELECT kind,severity FROM annotations_for('{uri.AbsoluteUri}', 'lint', 'info')").ToArray();
        rows.Length.Should().Be(1);
        rows[0]["kind"]!.ToString()!.ToLowerInvariant().Should().Be("lint");
        rows[0]["severity"]!.ToString()!.ToLowerInvariant().Should().Be("error");
    }

    [Test]
    public void SnippetMacro_WorksViaRawQuery()
    {
        using var store = new DuckDbDataStore();

        var art = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "digest",
            Size = 20,
            MediaType = SemanticMediaType.Parse("text/markdown"),
            Text = "Line 1\nLine 2\nLine 3\nLine 4"
        };
        store.UpsertArtifact(art);

        var uri = RepoUri.Parse("file:///repo/snippet.md");
        var doc = store.UpsertDocumentByUri(uri, new Node { Id = Guid.NewGuid(), Kind = "document", Uri = uri, ArtifactId = art.Id, Props = new JsonObject() });

        // Focus on line 2
        var rows = store.Query($"SELECT line_number, text, is_focus FROM snippet('{uri.AbsoluteUri}#line=2', 1);").ToArray();
        rows.Length.Should().BeGreaterThan(0);
        rows.Any(r => Convert.ToInt32(r["line_number"]) == 2 && (bool)r["is_focus"]!).Should().BeTrue();
    }
}
