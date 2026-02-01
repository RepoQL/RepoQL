using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;
using NodeModel = RepoQL.Contracts.Models.Node;

namespace RepoQL.Tests;

/// <summary>
/// Tests for search macro, verifying recall improvements via rescue features
/// (outline rescue and optional body rescue).
/// </summary>
internal class SearchTests
{
    private static IServiceProvider CreateTestServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RepositoryConfiguration { Path = Environment.CurrentDirectory });
        services.AddSingleton<UriRegistry>();
        services.AddSingleton<IEmbeddingProvider>(new DisabledTestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        return services.BuildServiceProvider();
    }

    private sealed class DisabledTestEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => false;
        public string Model => "test-disabled";
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

    [Test]
    public void Search_OutlineRescue_FindsDocsInStructureOnly()
    {
        // Arrange: Create documents where "config" appears in different places
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-hybrid-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbDataStore(dbPath, serviceProvider: CreateTestServiceProvider());

        // Document with "config" in structure but weak semantic match
        var structureDoc = RepoUri.Parse("file:///docs/api-reference.md");
        store.IndexArtifact(CreateParsedArtifact(
            structureDoc,
            "API Reference Documentation",
            "## Endpoints\n- /api/users\n- /api/config\n- /api/status",
            "This comprehensive API reference describes all available REST endpoints for the service."));

        // Document with "config" only in body (should need body rescue)
        var bodyDoc = RepoUri.Parse("file:///docs/deployment.md");
        store.IndexArtifact(CreateParsedArtifact(
            bodyDoc,
            "Deployment Guidelines",
            "Standard deployment procedures",
            "Before deploying, ensure all config files are validated and backed up properly."));

        // Unrelated document
        var unrelated = RepoUri.Parse("file:///docs/architecture.md");
        store.IndexArtifact(CreateParsedArtifact(
            unrelated,
            "System Architecture Overview",
            "## Components\n- Frontend\n- Backend\n- Database",
            "The system follows a three-tier architecture with clear separation of concerns."));

        // Act: Compare searches
        var hybridOutline = store.RawQuery(
            "SELECT uri, source, struct_mentions, body_mentions FROM search('config', enable_body_rescue := FALSE) LIMIT 20").ToList();

        var hybridBody = store.RawQuery(
            "SELECT uri, source, struct_mentions, body_mentions FROM search('config', enable_body_rescue := TRUE) LIMIT 20").ToList();

        // Assert: Verify rescue behavior
        var outlineUris = hybridOutline.Select(r => r["uri"]?.ToString()).ToHashSet();
        var bodyUris = hybridBody.Select(r => r["uri"]?.ToString()).ToHashSet();

        // Outline rescue should find the structure doc
        outlineUris.Should().Contain(structureDoc.AbsoluteUri, "outline rescue should find docs with term in structure");
    }

    [Test]
    public void Search_BodyRescue_FindsDocsInBodyOnly()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-hybrid-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbDataStore(dbPath, serviceProvider: CreateTestServiceProvider());

        // Document with "validation" only in body
        var bodyOnlyDoc = RepoUri.Parse("file:///docs/testing.md");
        store.IndexArtifact(CreateParsedArtifact(
            bodyOnlyDoc,
            "Testing Best Practices",
            "## Unit Tests\n## Integration Tests",
            "Always include input validation tests to ensure data integrity across all endpoints."));

        // Document with "validation" in structure
        var structureDoc = RepoUri.Parse("file:///docs/forms.md");
        store.IndexArtifact(CreateParsedArtifact(
            structureDoc,
            "Form Handling",
            "## Validation\n- Required fields\n- Format checks",
            "This document covers form handling patterns."));

        // Act
        var withBodyRescue = store.RawQuery(
            "SELECT uri, source, body_mentions FROM search('validation', enable_body_rescue := TRUE) LIMIT 20").ToList();

        var withoutBodyRescue = store.RawQuery(
            "SELECT uri, source, body_mentions FROM search('validation', enable_body_rescue := FALSE) LIMIT 20").ToList();

        // Assert
        var bodyUris = withBodyRescue.Select(r => r["uri"]?.ToString()).ToHashSet();
        bodyUris.Should().Contain(bodyOnlyDoc.AbsoluteUri, "body rescue should find docs with term only in body");
    }

    [Test]
    public void Search_BoostPattern_RanksMatchesHigher()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-hybrid-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbDataStore(dbPath, serviceProvider: CreateTestServiceProvider());

        // Document with boost pattern match
        var boostedDoc = RepoUri.Parse("file:///src/auth/jwt-handler.ts");
        store.IndexArtifact(CreateParsedArtifact(
            boostedDoc,
            "JWT Authentication Handler",
            "## JWT\n## Authentication",
            "Handles JWT token validation and refresh for the authentication system."));

        // Document without boost pattern
        var normalDoc = RepoUri.Parse("file:///src/auth/session.ts");
        store.IndexArtifact(CreateParsedArtifact(
            normalDoc,
            "Session Management",
            "## Sessions",
            "Manages user sessions for the authentication system."));

        // Act: Search with boost pattern for JWT
        var results = store.RawQuery(
            "SELECT uri, score, struct_mentions FROM search('authentication', boost_pattern := 'JWT') ORDER BY score DESC LIMIT 10").ToList();

        // Assert: JWT doc should rank higher due to boost
        results.Should().NotBeEmpty();
        var topResult = results[0]["uri"]?.ToString();
        topResult.Should().Contain("jwt", "boosted document should rank first");
    }

    [Test]
    public void Search_NegativePattern_DeranksMatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-hybrid-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbDataStore(dbPath, serviceProvider: CreateTestServiceProvider());

        // Test file (should be deranked)
        var testDoc = RepoUri.Parse("file:///tests/parser-tests.cs");
        store.IndexArtifact(CreateParsedArtifact(
            testDoc,
            "Parser Unit Tests",
            "## Test Cases",
            "Unit tests for the parser implementation."));

        // Source file (should rank higher)
        var srcDoc = RepoUri.Parse("file:///src/parser.cs");
        store.IndexArtifact(CreateParsedArtifact(
            srcDoc,
            "Parser Implementation",
            "## Parser",
            "Core parser implementation for processing input files."));

        // Act: Search with negative pattern for test
        var results = store.RawQuery(
            "SELECT uri, score, deranked FROM search('parser', negative_pattern := '(?i)test') ORDER BY score DESC LIMIT 10").ToList();

        // Assert: Source doc should rank higher, test doc should be deranked
        results.Should().NotBeEmpty();
        var srcResult = results.FirstOrDefault(r => r["uri"]?.ToString()?.Contains("src/") == true);
        var testResult = results.FirstOrDefault(r => r["uri"]?.ToString()?.Contains("tests/") == true);

        srcResult.Should().NotBeNull();
        testResult.Should().NotBeNull();

        var srcDeranked = Convert.ToBoolean(srcResult!["deranked"]);
        var testDeranked = Convert.ToBoolean(testResult!["deranked"]);

        srcDeranked.Should().BeFalse("source file should not be deranked");
        testDeranked.Should().BeTrue("test file should be deranked");
    }

    private static ParsedArtifact CreateParsedArtifact(RepoUri uri, string headline, string structure, string body)
    {
        var text = $"# {headline}\n\n{body}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = digest,
            Size = Encoding.UTF8.GetByteCount(text),
            MediaType = SemanticMediaType.Parse("text/markdown"),
            Text = text,
            Headline = headline,
            Summary = body.Length > 100 ? body[..100] : body,
            Structure = structure
        };

        var node = new NodeModel
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject(),
            Headline = headline,
            Structure = structure,
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
