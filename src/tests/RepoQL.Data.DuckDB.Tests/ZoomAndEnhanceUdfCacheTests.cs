using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB.UdfImplementations;
using System.Text;
using System.Text.Json;

namespace RepoQL.Data.DuckDB.Tests;

[NotInParallel(nameof(ZoomAndEnhanceUdfCacheTests))]
public sealed class ZoomAndEnhanceUdfCacheTests
{
    [Test]
    public void BuildCacheKey_UsesBoundedHash()
    {
        var largeText = new string('x', 200_000) + "needle";

        var keyA = ZoomAndEnhanceUdf.BuildCacheKeyForTests(largeText);
        var keyB = ZoomAndEnhanceUdf.BuildCacheKeyForTests(largeText + "diff");

        keyA.Length.Should().Be(32);
        keyA.Should().NotContain("needle");
        keyA.Should().NotContain("xxxxx");
        keyA.Should().NotBe(keyB);
    }

    [Test]
    public void AddCache_EnforcesHardSizeLimit()
    {
        ZoomAndEnhanceUdf.ClearCacheForTests();

        try
        {
            using var store = new DuckDbDataStore(":memory:");
            var udf = new ZoomAndEnhanceUdf(store);
            var maxCacheSize = ZoomAndEnhanceUdf.MaxCacheSizeForTests;
            var embedding = new[] { 1.0f, 0.5f, -0.5f };
            for (var i = 0; i < maxCacheSize + 250; i++)
            {
                udf.AddCacheForTests($"key-{i}", embedding);
            }

            ZoomAndEnhanceUdf.CacheEntryCountForTests.Should().BeLessThanOrEqualTo(maxCacheSize);
        }
        finally
        {
            ZoomAndEnhanceUdf.ClearCacheForTests();
        }
    }

    [Test]
    public void ZoomAndEnhance_QueryEmbeddingTimeout_ReturnsBaseRows()
    {
        using var store = new DuckDbDataStore(":memory:");
        var uri = RepoUri.Parse("file:///test/doc.md")!;
        store.IndexArtifact(uri, CreateTestArtifact(uri, CreateLineText(6)));

        var provider = new TimeoutEmbeddingProvider(timeoutQuery: true, timeoutBatch: false);
        var udf = new ZoomAndEnhanceUdf(store, provider);
        var chunks = BuildChunksJson(uri, startLine: 2, endLine: 5, score: 0.42);

        var rows = udf.ZoomAndEnhance(chunks, "needle", min_lines: 2, max_depth: 2, threshold: 0.2).ToList();

        rows.Should().HaveCount(1);
        rows[0].Uri.Should().Be(uri.AbsoluteUri);
        rows[0].StartLine.Should().Be(2);
        rows[0].EndLine.Should().Be(5);
        rows[0].Depth.Should().Be(0);
        rows[0].Score.Should().BeApproximately(0.42, 1e-9);
        provider.QueryCalls.Should().Be(1);
        provider.BatchCalls.Should().Be(0);
    }

    [Test]
    public void ZoomAndEnhance_PassageBatchTimeout_ReturnsParentRows()
    {
        ZoomAndEnhanceUdf.ClearCacheForTests();

        try
        {
            using var store = new DuckDbDataStore(":memory:");
            var uri = RepoUri.Parse("file:///test/doc.md")!;
            store.IndexArtifact(uri, CreateTestArtifact(uri, CreateLineText(12)));

            var provider = new TimeoutEmbeddingProvider(timeoutQuery: false, timeoutBatch: true);
            var udf = new ZoomAndEnhanceUdf(store, provider);
            var chunks = BuildChunksJson(uri, startLine: 1, endLine: 12, score: 0.73);

            var rows = udf.ZoomAndEnhance(chunks, "needle", min_lines: 2, max_depth: 2, threshold: 0.2).ToList();

            rows.Should().HaveCount(1);
            rows[0].Uri.Should().Be(uri.AbsoluteUri);
            rows[0].StartLine.Should().Be(1);
            rows[0].EndLine.Should().Be(12);
            rows[0].Depth.Should().Be(0);
            rows[0].Score.Should().BeApproximately(0.73, 1e-9);
            provider.QueryCalls.Should().Be(1);
            provider.BatchCalls.Should().Be(1);
        }
        finally
        {
            ZoomAndEnhanceUdf.ClearCacheForTests();
        }
    }

    private static string BuildChunksJson(RepoUri uri, int startLine, int endLine, double score)
    {
        var chunk = new
        {
            uri = uri.AbsoluteUri,
            start_line = startLine,
            end_line = endLine,
            score
        };

        return JsonSerializer.Serialize(new[] { chunk });
    }

    private static string CreateLineText(int lineCount)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= lineCount; i++)
        {
            if (i > 1)
                builder.Append('\n');
            builder.Append("line ");
            builder.Append(i);
        }

        return builder.ToString();
    }

    private static ParsedArtifact CreateTestArtifact(RepoUri uri, string textContent)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = textContent.Length,
                MediaType = SemanticMediaType.Parse("text/plain"),
                Text = textContent
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = "Test Document"
            },
            Children = [],
            Spans = [],
            Edges = []
        };
    }

    private sealed class TimeoutEmbeddingProvider(bool timeoutQuery, bool timeoutBatch) : IEmbeddingProvider
    {
        public string Model => "test-model";
        public int Dimension => 2;
        public bool Enabled => true;
        public int QueryCalls { get; private set; }
        public int BatchCalls { get; private set; }

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            if (timeoutQuery)
                throw new TimeoutException("query embedding timed out");

            return Task.FromResult<float[]?>(new[] { 1.0f, 0.0f });
        }

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(new[] { 1.0f, 0.0f });

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        {
            var result = new float[]?[texts?.Count ?? 0];
            for (var i = 0; i < result.Length; i++)
                result[i] = new[] { 1.0f, 0.0f };
            return Task.FromResult(result);
        }

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            if (timeoutBatch)
                throw new TimeoutException("batch embedding timed out");

            var result = new float[]?[texts?.Count ?? 0];
            for (var i = 0; i < result.Length; i++)
                result[i] = new[] { 1.0f, 0.0f };
            return Task.FromResult(result);
        }

        public Task<float[]?[]> EmbedPassageBatchAsync(
            IReadOnlyList<string>? texts,
            BatchEmbeddingProgress progress,
            CancellationToken cancellationToken = default)
            => EmbedPassageBatchAsync(texts, cancellationToken);
    }
}
