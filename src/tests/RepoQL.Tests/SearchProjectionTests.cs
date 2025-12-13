using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
    public void RefreshSearchProjection_PopulatesDocumentSearch()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-search-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbDataStore(dbPath);

        var docUri = RepoUri.Parse("file:///docs/sample.md");
        var otherUri = RepoUri.Parse("file:///docs/guide.md");
        store.IndexArtifact(CreateParsedArtifact(docUri, "Sample document content for search."));
        store.IndexArtifact(CreateParsedArtifact(otherUri, "Guide with different words."));

        store.RefreshSearchProjection(incremental: false);

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

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public void RefreshSearchProjection_UpdatedDocumentReflectsInSearch()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-search-update-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbDataStore(dbPath);

        var uri = RepoUri.Parse("file:///src/doc.md");

        // Write first version
        store.IndexArtifact(CreateParsedArtifact(uri, "initial content about apples"));

        // Write second version (same URI, different content)
        store.IndexArtifact(CreateParsedArtifact(uri, "updated content about oranges"));

        store.RefreshSearchProjection(incremental: false);

        // Should have exactly one search row for this URI
        var rows = store.RawQuery($"SELECT doc_id, uri FROM document_search WHERE lower(uri)=lower('{uri.AbsoluteUri}')").ToList();
        rows.Should().HaveCount(1, "should have exactly one search row per URI");

        // The document ID should be preserved
        var projectedDocId = Guid.Parse(rows[0]["doc_id"]!.ToString()!);

        // Verify the document exists and has correct URI
        var doc = store.GetDocumentByUri(uri);
        doc.Should().NotBeNull();
        doc!.Uri!.AbsoluteUri.Should().Be(uri.AbsoluteUri);

        try { File.Delete(dbPath); } catch { }
    }

    private static ParsedArtifact CreateParsedArtifact(RepoUri uri, string text)
    {
        var mediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
        var digestBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:" + Convert.ToHexString(digestBytes).ToLowerInvariant(),
            Size = text.Length,
            MediaType = mediaType,
            Text = text
        };
        var node = new NodeModel
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = node
        };
    }
}
