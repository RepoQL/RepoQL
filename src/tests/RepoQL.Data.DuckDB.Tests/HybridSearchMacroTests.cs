using AwesomeAssertions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Embeddings;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class SearchMacroTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _store;

    public SearchMacroTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new TestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        services.AddSingleton<UriRegistry>();
        _serviceProvider = services.BuildServiceProvider();
        _store = new DuckDbDataStore(serviceProvider: _serviceProvider);

        // Create sample documents for testing
        CreateSampleDocuments();
    }

    public void Dispose()
    {
        _store?.Dispose();
        _serviceProvider?.Dispose();
    }

    private class TestEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => "test-model";
        public int Dimension => 384;
        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private void CreateSampleDocuments()
    {
        // Document 1: Contains "database" and "DuckDB" (should be boosted)
        CreateDocument(
            "file:///docs/database-guide.md",
            "text/markdown",
            "Database Guide",
            "This document explains how to work with DuckDB database connections",
            "# Database Guide\nLearn about DuckDB and database connections"
        );

        // Document 2: Contains "database" but not "DuckDB"
        CreateDocument(
            "file:///src/database-utils.cs",
            "text/x-csharp",
            "Database Utilities",
            "General database utility functions",
            "public class DatabaseUtils { void Connect() { } }"
        );

        // Document 3: Contains "index" but is a test file (should be deranked)
        CreateDocument(
            "file:///tests/index-tests.cs",
            "text/x-csharp",
            "Index Tests",
            "Tests for indexing functionality",
            "public class IndexTests { [Test] void TestIndexing() { } }"
        );

        // Document 4: Contains "index" but is not a test file
        CreateDocument(
            "file:///src/indexer.cs",
            "text/x-csharp",
            "Indexer",
            "Main indexing implementation",
            "public class Indexer { void BuildIndex() { } }"
        );

        // Document 5: Contains "parser" and "markdown" (should be boosted)
        CreateDocument(
            "file:///src/markdown-parser.cs",
            "text/x-csharp",
            "Markdown Parser",
            "Parser implementation for markdown files",
            "public class MarkdownParser { void Parse() { } }"
        );

        // Document 6: Contains "parser" but is a test file (should be deranked)
        CreateDocument(
            "file:///tests/parser-tests.cs",
            "text/x-csharp",
            "Parser Tests",
            "Tests for parser functionality",
            "public class ParserTests { [Test] void TestParsing() { } }"
        );

        // Document 7: Contains "parser" without markdown
        CreateDocument(
            "file:///src/json-parser.cs",
            "text/x-csharp",
            "JSON Parser",
            "Parser implementation for JSON files",
            "public class JsonParser { void Parse() { } }"
        );
    }

    private void CreateDocument(string uri, string mediaType, string headline, string summary, string content)
    {
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = $"digest-{uri}",
            Size = content.Length,
            Text = content,
            Headline = headline,
            Summary = summary,
            Structure = summary,
            MediaType = SemanticMediaType.Parse(mediaType)
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse(uri, out var parsedUri) ? parsedUri : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact { Artifact = artifact, DocumentNode = node });
    }

    [Test]
    public void HybridSearch_WithoutBoost_ReturnsResults()
    {
        var results = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score
              FROM search('database')
              ORDER BY score DESC
              LIMIT 5",
            r => (uri: r.GetString(0), score: r.GetDouble(1)));

        // Should return database-related documents
        results.Should().NotBeEmpty();
        results.Select(r => r.uri).Should().Contain(u => u.Contains("database"));
    }

    [Test]
    public void HybridSearch_WithBoostPattern_BoostsMatchingDocs()
    {
        // First, get results without boost
        var resultsWithoutBoost = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score
              FROM search('database')
              ORDER BY score DESC
              LIMIT 5",
            r => (uri: r.GetString(0), score: r.GetDouble(1)));

        // Now get results with boost pattern
        var resultsWithBoost = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score, struct_mentions
              FROM search('database', boost_pattern := 'DuckDB|connection')
              ORDER BY score DESC
              LIMIT 5",
            r => (uri: r.GetString(0), score: r.GetDouble(1), mentions: r.GetInt32(2)));

        // The document with "DuckDB" should be boosted and rank higher
        resultsWithBoost.Should().NotBeEmpty();

        // The database-guide.md should have struct_mentions > 0 since it contains "DuckDB" and "connection"
        var duckdbDoc = resultsWithBoost.FirstOrDefault(r => r.uri.Contains("database-guide"));
        duckdbDoc.Should().NotBe(default);
        duckdbDoc.mentions.Should().BeGreaterThan(0);
    }

    [Test]
    public void HybridSearch_WithNegativePattern_DeranksMatchingDocs()
    {
        var results = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score, deranked
              FROM search('index', negative_pattern := '(?i)test')
              ORDER BY score DESC
              LIMIT 5",
            r => (uri: r.GetString(0), score: r.GetDouble(1), deranked: r.GetBoolean(2)));

        // Should return index-related documents
        results.Should().NotBeEmpty();

        // Test files should be marked as deranked
        var testFiles = results.Where(r => r.uri.Contains("test")).ToList();
        foreach (var testFile in testFiles)
        {
            testFile.deranked.Should().BeTrue("test files should be deranked");
        }

        // Non-test files should not be deranked
        var nonTestFiles = results.Where(r => !r.uri.Contains("test")).ToList();
        foreach (var nonTestFile in nonTestFiles)
        {
            nonTestFile.deranked.Should().BeFalse("non-test files should not be deranked");
        }

        // Non-deranked files should score higher than deranked ones (assuming similar base scores)
        var highestNonTestScore = nonTestFiles.Max(r => r.score);
        var highestTestScore = testFiles.Any() ? testFiles.Max(r => r.score) : 0;

        if (testFiles.Any() && nonTestFiles.Any())
        {
            highestNonTestScore.Should().BeGreaterThan(highestTestScore * 0.9,
                "non-test files should generally rank higher than deranked test files");
        }
    }

    [Test]
    public void HybridSearch_WithCombinedBoostAndNegative_AppliesBothFilters()
    {
        var results = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score, struct_mentions, deranked
              FROM search('parser', boost_pattern := 'markdown', negative_pattern := '(?i)test')
              ORDER BY score DESC
              LIMIT 8",
            r => (uri: r.GetString(0), score: r.GetDouble(1), mentions: r.GetInt32(2), deranked: r.GetBoolean(3)));

        results.Should().NotBeEmpty();

        // markdown-parser.cs should be boosted (has "markdown" in it)
        var markdownParser = results.FirstOrDefault(r => r.uri.Contains("markdown-parser"));
        if (markdownParser != default)
        {
            markdownParser.mentions.Should().BeGreaterThan(0, "markdown parser should have boost pattern mentions");
            markdownParser.deranked.Should().BeFalse("markdown parser is not a test file");

            // It should rank highest due to boost and no deranking
            markdownParser.score.Should().Be(results.Max(r => r.score),
                "boosted non-test file should rank highest");
        }

        // parser-tests.cs should be deranked
        var parserTests = results.FirstOrDefault(r => r.uri.Contains("parser-tests"));
        if (parserTests != default)
        {
            parserTests.deranked.Should().BeTrue("test files should be deranked");
        }

        // Verify that boosted non-test files rank higher than test files
        var boostedNonTest = results.Where(r => r.mentions > 0 && !r.deranked).ToList();
        var testFilesResult = results.Where(r => r.deranked).ToList();

        if (boostedNonTest.Any() && testFilesResult.Any())
        {
            var minBoostedScore = boostedNonTest.Min(r => r.score);
            var maxTestScore = testFilesResult.Max(r => r.score);
            minBoostedScore.Should().BeGreaterThan(maxTestScore * 0.8,
                "boosted non-test files should generally rank higher than deranked test files");
        }
    }

    [Test]
    public void HybridSearch_WithScope_FiltersResults()
    {
        var results = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score
              FROM search('parser', scope := 'file:///src/%')
              ORDER BY score DESC",
            r => (uri: r.GetString(0), score: r.GetDouble(1)));

        results.Should().NotBeEmpty();

        // All results should be from /src/ directory
        foreach (var result in results)
        {
            result.uri.Should().StartWith("file:///src/",
                "scope should filter to only /src/ files");
        }

        // Should not contain test files
        results.Should().NotContain(r => r.uri.Contains("tests/"),
            "scope should exclude test directory");
    }

    [Test]
    public void HybridSearch_RespectsKAfterRescueExpansion()
    {
        var results = _store.Read(
            @"SELECT uri
              FROM search(
                  'no_such_keyword',
                  boost_pattern := 'database|index|parser',
                  k := 2
              )",
            r => r.GetString(0));

        results.Should().HaveCount(2, "search() should enforce k after rescue tiers are added");
    }

    [Test]
    public void SearchCandidates_WithUriLike_FiltersResults()
    {
        var results = _store.Read(
            @"SELECT uri
              FROM _search_candidates('parser', k := 50, uri_like := 'file:///src/%')
              ORDER BY score DESC",
            r => r.GetString(0));

        results.Should().NotBeEmpty();
        foreach (var uri in results)
        {
            uri.Should().StartWith("file:///src/",
                "uri_like should constrain candidate generation to scope");
        }
    }
}

public class SearchMacroEmbeddingDimensionTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _store;
    private const int QueryDimension = 3;
    private readonly string _uriGood = "file:///docs/good.md";
    private readonly string _uriBad = "file:///docs/bad.md";

    public SearchMacroEmbeddingDimensionTests()
    {
        var provider = new HashedEmbeddingProvider(QueryDimension, modelName: "test-hash-3");
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(provider);
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        services.AddSingleton<UriRegistry>();
        _serviceProvider = services.BuildServiceProvider();
        _store = new DuckDbDataStore(embeddingProvider: provider, serviceProvider: _serviceProvider);

        var docGood = CreateDocument(
            _uriGood,
            mediaType: "text/markdown",
            headline: "Good",
            summary: "Document about database connections",
            content: "# Good\nDatabase connections and setup."
        );

        var docBad = CreateDocument(
            _uriBad,
            mediaType: "text/markdown",
            headline: "Bad",
            summary: "Another document about database connections",
            content: "# Bad\nDatabase connections and setup."
        );

        // Seed embeddings: one matches the query dimension, one is mismatched.
        // The search macro should ignore the mismatched embeddings (instead of failing cosine similarity).
        _store.WriteEmbeddings(new List<DocumentEmbedding>
        {
            new(
                DocumentId: docGood,
                NodeId: docGood,
                ChunkIndex: 0,
                EmbeddingType: DocumentEmbedding.TypeStructure,
                Uri: _uriGood,
                Scope: DocumentEmbedding.ScopeDocument,
                Vector: new float[] { 1f, 0f, 0f },
                Model: provider.Model,
                Dimension: QueryDimension),
            new(
                DocumentId: docGood,
                NodeId: docGood,
                ChunkIndex: 0,
                EmbeddingType: DocumentEmbedding.TypeFull,
                Uri: _uriGood,
                Scope: DocumentEmbedding.ScopeDocument,
                Vector: new float[] { 0f, 1f, 0f },
                Model: provider.Model,
                Dimension: QueryDimension,
                StartByte: 0,
                EndByte: 10),

            // Mismatched dimension embeddings for the second doc (both structure and full).
            new(
                DocumentId: docBad,
                NodeId: docBad,
                ChunkIndex: 0,
                EmbeddingType: DocumentEmbedding.TypeStructure,
                Uri: _uriBad,
                Scope: DocumentEmbedding.ScopeDocument,
                Vector: new float[] { 1f, 0f, 0f, 0f, 0f },
                Model: "other-model",
                Dimension: 5),
            new(
                DocumentId: docBad,
                NodeId: docBad,
                ChunkIndex: 0,
                EmbeddingType: DocumentEmbedding.TypeFull,
                Uri: _uriBad,
                Scope: DocumentEmbedding.ScopeDocument,
                Vector: new float[] { 0f, 1f, 0f, 0f, 0f },
                Model: "other-model",
                Dimension: 5,
                StartByte: 0,
                EndByte: 10)
        });
    }

    public void Dispose()
    {
        _store?.Dispose();
        _serviceProvider?.Dispose();
    }

    private Guid CreateDocument(string uri, string mediaType, string headline, string summary, string content)
    {
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = $"digest-{uri}",
            Size = content.Length,
            Text = content,
            Headline = headline,
            Summary = summary,
            Structure = summary,
            MediaType = SemanticMediaType.Parse(mediaType)
        };

        var nodeId = Guid.NewGuid();
        var node = new Node
        {
            Id = nodeId,
            Kind = "document",
            Uri = RepoUri.TryParse(uri, out var parsedUri) ? parsedUri : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };

        _store.IndexArtifact(new ParsedArtifact { Artifact = artifact, DocumentNode = node });
        return nodeId;
    }

    [Test]
    public void Search_WithMixedEmbeddingDimensions_DoesNotThrow()
    {
        var mismatched = _store.Read(
            $"SELECT COUNT(*) FROM document_embedding WHERE dim <> {QueryDimension}",
            r => r.GetInt64(0))[0];
        mismatched.Should().BeGreaterThan(0, "test setup should include mismatched-dimension embeddings");

        var results = _store.Read(
            @"SELECT uri, ROUND(score, 3) AS score
              FROM search('database')
              ORDER BY score DESC
              LIMIT 5",
            r => (uri: r.GetString(0), score: r.GetDouble(1)));

        results.Should().NotBeEmpty();
        results.Select(r => r.uri).Should().Contain(_uriGood);
        results.Select(r => r.uri).Should().Contain(_uriBad);
    }
}
