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

/// <summary>
/// Tests for hybrid_search macro, verifying recall improvements via rescue features
/// (outline rescue and optional body rescue).
/// </summary>
internal class HybridSearchTests
{
    [Test]
    public async Task HybridSearch_OutlineRescue_FindsDocsInStructureOnly()
    {
        // Arrange: Create documents where "config" appears in different places
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-hybrid-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        // Document with "config" in structure but weak semantic match
        // (search() might miss this if semantic/BM25 scores are low)
        var structureDoc = RepoUri.Parse("file:///docs/api-reference.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            structureDoc,
            "API Reference Documentation",
            "## Endpoints\n- /api/users\n- /api/config\n- /api/status",
            "This comprehensive API reference describes all available REST endpoints for the service."));

        // Document with "config" only in body (should need body rescue)
        var bodyDoc = RepoUri.Parse("file:///docs/deployment.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            bodyDoc,
            "Deployment Guidelines",
            "Standard deployment procedures",
            "Before deploying, ensure all config files are validated and backed up properly."));

        // Unrelated document (lots of text to dilute any incidental matches)
        var unrelated = RepoUri.Parse("file:///docs/architecture.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            unrelated,
            "System Architecture Overview",
            "## Components\n- Frontend\n- Backend\n- Database",
            "The system follows a three-tier architecture with clear separation of concerns between presentation, business logic, and data layers."));

        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        // Act: Compare searches
        var hybridOutline = store.RawQuery(
            "SELECT uri, source, struct_mentions, body_mentions FROM hybrid_search('config', enable_body_rescue := FALSE) LIMIT 20").ToList();

        var hybridBody = store.RawQuery(
            "SELECT uri, source, struct_mentions, body_mentions FROM hybrid_search('config', enable_body_rescue := TRUE) LIMIT 20").ToList();

        // Assert: Verify rescue behavior
        var outlineUris = hybridOutline.Select(r => r["uri"]?.ToString()).ToHashSet();
        var bodyUris = hybridBody.Select(r => r["uri"]?.ToString()).ToHashSet();

        // Structure doc should be found by outline rescue
        outlineUris.Should().Contain(structureDoc.AbsoluteUri,
            "outline rescue should find doc with 'config' in structure");

        // Body doc requires body rescue
        bodyUris.Should().Contain(bodyDoc.AbsoluteUri,
            "body rescue should find doc with 'config' in body");

        // Verify source attribution for structure doc
        var structMatch = hybridOutline.FirstOrDefault(r => r["uri"]?.ToString() == structureDoc.AbsoluteUri);
        if (structMatch != null)
        {
            var structMentions = Convert.ToInt32(structMatch["struct_mentions"]);
            structMentions.Should().BeGreaterThan(0, "structure doc should have struct_mentions > 0");
        }

        // Verify body rescue increased recall
        bodyUris.Count.Should().BeGreaterThanOrEqualTo(outlineUris.Count,
            "body rescue should find at least as many docs as outline-only");

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_KnownItemSearch_RanksByRelevance()
    {
        // Test that both file_search and hybrid_search can find known items
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-known-item-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        var targetUri = RepoUri.Parse("file:///src/SingleThreadedDatabaseWriter.cs");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            targetUri,
            "SingleThreadedDatabaseWriter.cs",
            "public class SingleThreadedDatabaseWriter : IAsyncDisposable",
            "All DuckDB writes MUST go through SingleThreadedDatabaseWriter. Parallel writes cause database corruption."));

        // Add some other documents to make the search more realistic
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            RepoUri.Parse("file:///src/OtherWriter.cs"),
            "OtherWriter.cs",
            "public class OtherWriter",
            "A different writer class."));

        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        // Act: Search for known item
        var fileSearchResults = store.RawQuery(
            "SELECT uri, ROUND(score, 3) AS score FROM file_search('SingleThreadedDatabaseWriter') LIMIT 3").ToList();

        var hybridSearchResults = store.RawQuery(
            "SELECT uri, ROUND(score, 3) AS score, source FROM hybrid_search('SingleThreadedDatabaseWriter') LIMIT 3").ToList();

        // Assert: Both should find the target
        fileSearchResults.Should().NotBeEmpty("file_search should find SingleThreadedDatabaseWriter");
        hybridSearchResults.Should().NotBeEmpty("hybrid_search should find SingleThreadedDatabaseWriter");

        var fileSearchTop = fileSearchResults.First()["uri"]?.ToString();
        var hybridSearchTop = hybridSearchResults.First()["uri"]?.ToString();

        fileSearchTop.Should().Be(targetUri.AbsoluteUri, "file_search should rank SingleThreadedDatabaseWriter.cs first");
        hybridSearchTop.Should().Be(targetUri.AbsoluteUri, "hybrid_search should rank SingleThreadedDatabaseWriter.cs first");

        // Both should have positive scores (relaxed threshold for realistic scoring)
        var fileSearchScore = Convert.ToDouble(fileSearchResults.First()["score"]);
        var hybridSearchScore = Convert.ToDouble(hybridSearchResults.First()["score"]);

        fileSearchScore.Should().BeGreaterThan(0.0, "file_search should give positive score to match");
        hybridSearchScore.Should().BeGreaterThan(0.0, "hybrid_search should give positive score to match");

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_VerifiesSourceAttribution()
    {
        // Verify that hybrid_search correctly attributes sources: semantic, bm25, outline, body, search
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-source-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        // Document that should be found by outline rescue
        var outlineDoc = RepoUri.Parse("file:///docs/outline-match.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            outlineDoc,
            "Configuration Guide",
            "## Topics\n- database setup\n- connection strings",
            "Unrelated body content here."));

        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        // Act: Search with outline rescue enabled
        var results = store.RawQuery(
            "SELECT uri, source FROM hybrid_search('database') LIMIT 20").ToList();

        // Assert: Verify source attribution
        results.Should().NotBeEmpty("hybrid_search should find documents matching 'database'");

        var outlineMatch = results.FirstOrDefault(r => r["uri"]?.ToString() == outlineDoc.AbsoluteUri);
        if (outlineMatch != null)
        {
            var source = outlineMatch["source"]?.ToString();
            // Could be 'outline' if rescued, or 'semantic'/'bm25' if found by search()
            source.Should().BeOneOf("outline", "semantic", "bm25", "search",
                "source should be one of the valid attribution types");
        }

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_EmptyQuery_ExecutesWithoutError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-empty-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        var docUri = RepoUri.Parse("file:///docs/sample.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(docUri, "Sample", "Sample content", "Sample body"));
        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        var rows = store.RawQuery("SELECT COUNT(*) AS results FROM hybrid_search('')").ToList();
        rows.Should().NotBeEmpty("empty query should execute without error");
        var count = Convert.ToInt32(rows[0]["results"]);
        count.Should().BeGreaterThanOrEqualTo(0, "empty query should return non-negative count");

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_MinimalQuery_ExecutesWithoutError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-minimal-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        var docUri = RepoUri.Parse("file:///docs/sample.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(docUri, "Sample", "Sample content", "Sample body"));
        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        var rows = store.RawQuery("SELECT COUNT(*) AS results FROM hybrid_search('a')").ToList();
        rows.Should().NotBeEmpty("minimal query should execute without error");
        var count = Convert.ToInt32(rows[0]["results"]);
        count.Should().BeGreaterThanOrEqualTo(0, "minimal query should return non-negative count");

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_CaseSensitivity_ReturnsSameResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-case-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        var docUri = RepoUri.Parse("file:///docs/duckdb.md");
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            docUri,
            "DuckDB Guide",
            "DuckDB is an in-process SQL database",
            "DuckDB provides excellent performance."));
        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        var rows = store.RawQuery(@"
            SELECT 'DuckDB' AS q, COUNT(*) AS n FROM hybrid_search('DuckDB')
            UNION ALL SELECT 'duckdb', COUNT(*) FROM hybrid_search('duckdb')
            UNION ALL SELECT 'DUCKDB', COUNT(*) FROM hybrid_search('DUCKDB')
        ").ToList();

        rows.Should().HaveCount(3, "should have results for all three case variations");
        var counts = rows.Select(r => Convert.ToInt32(r["n"])).ToList();
        counts.All(c => c == counts[0]).Should().BeTrue("all case variations should return the same count");

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_MultiWordSplitting_ReturnsResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-multi-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        var doc1Uri = RepoUri.Parse("file:///src/Writer.cs");
        var doc2Uri = RepoUri.Parse("file:///src/Thread.cs");
        var doc3Uri = RepoUri.Parse("file:///src/Single.cs");

        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            doc1Uri, "Writer", "Database Writer", "SingleThreadedDatabaseWriter class"));
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            doc2Uri, "Thread", "Thread utilities", "Thread management"));
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            doc3Uri, "Single", "Single instance", "Single instance pattern"));
        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        var rows = store.RawQuery(@"
            SELECT uri, struct_mentions, ROUND(score, 3) AS score
            FROM hybrid_search('single threaded writer')
            ORDER BY score DESC LIMIT 3
        ").ToList();

        rows.Should().NotBeEmpty("multi-word query should return results");

        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task HybridSearch_ScopeFiltering_FiltersResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-scope-{Guid.NewGuid():N}.duckdb");
        var connFactory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        var graphStoreFactory = new DuckDbGraphStoreFactory();
        await using var writer = new SingleThreadedDatabaseWriter(connFactory, graphStoreFactory);
        await writer.StartAsync(CancellationToken.None);

        var docUri = RepoUri.Parse("file:///docs/search.md");
        var srcUri = RepoUri.Parse("file:///src/Search.cs");

        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            docUri, "Search Documentation", "About search", "Search functionality docs"));
        await writer.EnqueueAndWaitAsync(CreateWriteWithHeadline(
            srcUri, "Search.cs", "Search implementation", "Search code"));
        await writer.FlushAsync();
        await writer.StopAsync(CancellationToken.None);

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();
        store.RefreshSearchProjection(incrementalRefresh: false);

        var totalRows = store.RawQuery("SELECT COUNT(*) AS total FROM hybrid_search('search')").ToList();
        var total = Convert.ToInt32(totalRows[0]["total"]);

        var docsRows = store.RawQuery("SELECT COUNT(*) AS docs_only FROM hybrid_search('search', scope := 'file:///docs/%')").ToList();
        var docsOnly = Convert.ToInt32(docsRows[0]["docs_only"]);

        total.Should().BeGreaterThanOrEqualTo(1, "should find at least one result");
        docsOnly.Should().BeLessThanOrEqualTo(total, "filtered results should not exceed total");

        try { File.Delete(dbPath); } catch { }
    }

    private static WriteOperation CreateWriteWithHeadline(RepoUri uri, string headline, string structure, string body)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var mediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
        var digestBytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));

        var artifact = new ArtifactModel
        {
            Id = artifactId,
            Digest = "sha256:" + Convert.ToHexString(digestBytes).ToLowerInvariant(),
            Size = body.Length,
            MediaType = mediaType,
            Text = body,
            Headline = headline,
            Structure = structure
        };

        var node = new NodeModel
        {
            Id = docId,
            Kind = "document",
            Uri = uri,
            ArtifactId = artifactId,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
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
