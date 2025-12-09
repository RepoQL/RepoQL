using DuckDB.NET.Data;
using AwesomeAssertions;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Metrics;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class HybridSearchMacroTests : IDisposable
{
    private readonly DuckDBConnection _connection;
    private readonly DuckDbGraphStore _store;
    private readonly IndexingMetrics _metrics;

    public HybridSearchMacroTests()
    {
        _connection = new DuckDBConnection("Data Source=:memory:");
        _connection.Open();
        _metrics = new IndexingMetrics();
        _store = new DuckDbGraphStore(_connection, _metrics);
        _store.EnsureSchema();

        // Create sample documents for testing
        CreateSampleDocuments();
    }

    public void Dispose()
    {
        _store?.Dispose();
        _metrics?.Dispose();
        _connection?.Dispose();
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
        _store.UpsertArtifact(artifact);

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.TryParse(uri, out var parsedUri) ? parsedUri : null,
            ArtifactId = artifact.Id,
            Props = new JsonObject()
        };
        _store.UpsertNode(node);
    }

    [Test]
    public void HybridSearch_WithoutBoost_ReturnsResults()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uri, ROUND(score, 3) AS score
            FROM hybrid_search('database')
            ORDER BY score DESC
            LIMIT 5";

        using var reader = cmd.ExecuteReader();
        var results = new List<(string uri, double score)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetDouble(1)
            ));
        }

        // Should return database-related documents
        results.Should().NotBeEmpty();
        results.Select(r => r.uri).Should().Contain(u => u.Contains("database"));
    }

    [Test]
    public void HybridSearch_WithBoostPattern_BoostsMatchingDocs()
    {
        using var cmd = _connection.CreateCommand();

        // First, get results without boost
        cmd.CommandText = @"
            SELECT uri, ROUND(score, 3) AS score
            FROM hybrid_search('database')
            ORDER BY score DESC
            LIMIT 5";

        using var reader1 = cmd.ExecuteReader();
        var resultsWithoutBoost = new List<(string uri, double score)>();
        while (reader1.Read())
        {
            resultsWithoutBoost.Add((
                reader1.GetString(0),
                reader1.GetDouble(1)
            ));
        }
        reader1.Close();

        // Now get results with boost pattern
        cmd.CommandText = @"
            SELECT uri, ROUND(score, 3) AS score, struct_mentions
            FROM hybrid_search('database', boost_pattern := 'DuckDB|connection')
            ORDER BY score DESC
            LIMIT 5";

        using var reader2 = cmd.ExecuteReader();
        var resultsWithBoost = new List<(string uri, double score, int mentions)>();
        while (reader2.Read())
        {
            resultsWithBoost.Add((
                reader2.GetString(0),
                reader2.GetDouble(1),
                reader2.GetInt32(2)
            ));
        }

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
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uri, ROUND(score, 3) AS score, deranked
            FROM hybrid_search('index', negative_pattern := '(?i)test')
            ORDER BY score DESC
            LIMIT 5";

        using var reader = cmd.ExecuteReader();
        var results = new List<(string uri, double score, bool deranked)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetBoolean(2)
            ));
        }

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
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uri, ROUND(score, 3) AS score, struct_mentions, deranked
            FROM hybrid_search('parser', boost_pattern := 'markdown', negative_pattern := '(?i)test')
            ORDER BY score DESC
            LIMIT 8";

        using var reader = cmd.ExecuteReader();
        var results = new List<(string uri, double score, int mentions, bool deranked)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetInt32(2),
                reader.GetBoolean(3)
            ));
        }

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
        var testFiles = results.Where(r => r.deranked).ToList();

        if (boostedNonTest.Any() && testFiles.Any())
        {
            var minBoostedScore = boostedNonTest.Min(r => r.score);
            var maxTestScore = testFiles.Max(r => r.score);
            minBoostedScore.Should().BeGreaterThan(maxTestScore * 0.8,
                "boosted non-test files should generally rank higher than deranked test files");
        }
    }

    [Test]
    public void HybridSearch_WithScope_FiltersResults()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uri, ROUND(score, 3) AS score
            FROM hybrid_search('parser', scope := 'file:///src/%')
            ORDER BY score DESC";

        using var reader = cmd.ExecuteReader();
        var results = new List<(string uri, double score)>();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetDouble(1)
            ));
        }

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
}
