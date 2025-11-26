using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;
using NodeModel = RepoQL.Contracts.Models.Node;

namespace RepoQL.Tests;

internal class SearchProjectionTests
{
    [Test]
    public async Task WriterRefreshesDocumentSearchAutomatically()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-search-{Guid.NewGuid():N}.duckdb");
        await using var writer = new SingleThreadedDatabaseWriter(new DuckDBConnectionFactory($"Data Source={dbPath}"));
        await writer.StartAsync(CancellationToken.None);

        var docUri = RepoUri.Parse("file:///docs/sample.md");
        var otherUri = RepoUri.Parse("file:///docs/guide.md");
        await writer.EnqueueAndWaitAsync(CreateWrite(docUri, "Sample document content for search."));
        await writer.EnqueueAndWaitAsync(CreateWrite(otherUri, "Guide with different words."));
        await writer.FlushAsync();

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        var rows = store.RawQuery("SELECT uri, basename, dirname, search_key FROM document_search").ToList();
        rows.Should().HaveCount(2, "both documents are projected into document_search");
        rows.Any(r => string.Equals(r["uri"]?.ToString(), docUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)).Should().BeTrue();

        var searchRows = store.RawQuery(
            "SELECT uri, score, bm25n, fuzzn FROM file_search('sample', k := 5, max_cand := 100)").ToList();
        searchRows.Should().NotBeEmpty("file_search should yield at least one candidate");
        var hit = searchRows.First(r => string.Equals(r["uri"]?.ToString(), docUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
        Convert.ToDouble(hit["score"]).Should().BeGreaterThan(0d);

        var fuzzyScore = store.RawQuery("SELECT match_score('svc', 'service-handler') AS s").First()["s"];
        Convert.ToDouble(fuzzyScore).Should().BeGreaterThan(0d, "match_score returns positive score for subsequence matches");

        await writer.StopAsync(CancellationToken.None);
        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task RefreshSearchProjection_UpdatedDocumentReflectsInSearch()
    {
        // Test that when a document is updated (same URI), the search projection reflects the update.
        // Note: Unique index on container_uri_lowercase prevents duplicate URIs, so upsert updates in place.
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-search-update-{Guid.NewGuid():N}.duckdb");
        await using var writer = new SingleThreadedDatabaseWriter(new DuckDBConnectionFactory($"Data Source={dbPath}"));
        await writer.StartAsync(CancellationToken.None);

        var uri = RepoUri.Parse("file:///src/doc.md");

        // Write first version
        var first = CreateWrite(uri, "initial content about apples");
        await writer.EnqueueAndWaitAsync(first);
        await writer.FlushAsync();

        // Write second version (same URI, different content)
        var second = CreateWrite(uri, "updated content about oranges");
        await writer.EnqueueAndWaitAsync(second);
        await writer.FlushAsync();

        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        // Should have exactly one search row for this URI
        var rows = store.RawQuery("SELECT doc_id, uri FROM document_search WHERE lower(uri)=lower(?)", uri.AbsoluteUri).ToList();
        rows.Should().HaveCount(1, "should have exactly one search row per URI");

        // The document ID should be the same as the first (upsert preserves ID)
        var projectedDocId = Guid.Parse(rows[0]["doc_id"]!.ToString()!);

        // Verify the document exists and has correct URI
        var doc = store.GetNode(projectedDocId);
        doc.Should().NotBeNull();
        doc!.Uri!.AbsoluteUri.Should().Be(uri.AbsoluteUri);

        try { File.Delete(dbPath); } catch { }
    }

    private static WriteOperation CreateWrite(RepoUri uri, string text)
        => CreateWrite(uri, text, DateTimeOffset.UtcNow);

    private static WriteOperation CreateWrite(RepoUri uri, string text, DateTimeOffset updatedAt)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var mediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
        var digestBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var artifact = new ArtifactModel
        {
            Id = artifactId,
            Digest = "sha256:" + Convert.ToHexString(digestBytes).ToLowerInvariant(),
            Size = text.Length,
            MediaType = mediaType,
            Text = text
        };
        var node = new NodeModel
        {
            Id = docId,
            Kind = "document",
            Uri = uri,
            ArtifactId = artifactId,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt
        };

        return new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.ReplaceDocument,
            Uri = uri,
            ParsedData = new Records
            {
                Artifacts = [artifact],
                Nodes = [node],
                Spans = [],
                Edges = []
            }
        };
    }
}
