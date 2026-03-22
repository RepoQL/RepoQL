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

    [Test]
    public void RunPrecomputedTree_SingleBatchEmbedCall()
    {
        ZoomAndEnhanceUdf.ClearCacheForTests();

        try
        {
            using var store = new DuckDbDataStore(":memory:");
            var uri = RepoUri.Parse("file:///test/tree.md")!;
            store.IndexArtifact(uri, CreateTestArtifact(uri, CreateLineText(32)));

            var provider = new TimeoutEmbeddingProvider(timeoutQuery: false, timeoutBatch: false);
            var udf = new ZoomAndEnhanceUdf(store, provider);
            var chunks = BuildChunksJson(uri, startLine: 1, endLine: 32, score: 0.7);

            var rows = udf.ZoomAndEnhance(chunks, "needle", min_lines: 2, max_depth: 3, threshold: 0.1).ToList();

            rows.Should().HaveCountGreaterThan(0);
            provider.QueryCalls.Should().Be(1);
            provider.BatchCalls.Should().Be(1,
                "the precomputed tree should embed all candidate splits in a single batch");
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

    [Test]
    public void ParseQueryTerms_Filters_Short_Terms_And_Deduplicates()
    {
        var terms = ZoomAndEnhanceUdf.ParseQueryTerms("a validate b token validate");

        terms.Should().HaveCount(2);
        terms.Should().Contain("validate");
        terms.Should().Contain("token");
    }

    [Test]
    [Arguments("hello world foo", new[] { "hello", "world" }, 1.0)]
    [Arguments("hello foo bar", new[] { "hello", "world" }, 0.5)]
    [Arguments("foo bar baz", new[] { "hello", "world" }, 0.0)]
    public void TermCoverage_Computes_Fraction_Of_Matching_Terms(
        string text, string[] terms, double expected)
    {
        ZoomAndEnhanceUdf.TermCoverage(text, terms)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void ZoomAndEnhance_Boosts_Chunks_Containing_Query_Terms()
    {
        ZoomAndEnhanceUdf.ClearCacheForTests();

        try
        {
            using var store = new DuckDbDataStore(":memory:");
            var uri = RepoUri.Parse("file:///test/doc.cs")!;

            // Lines 1-6: no "needle"; lines 7-12: contain "needle"
            var content = string.Join("\n",
                "alpha bravo charlie",
                "delta echo foxtrot",
                "golf hotel india",
                "juliet kilo lima",
                "mike november oscar",
                "papa quebec romeo",
                "sierra needle tango",
                "uniform victor whiskey",
                "xray yankee zulu",
                "one two three",
                "four five six",
                "seven needle eight");

            store.IndexArtifact(uri, CreateTestArtifact(uri, content));

            var provider = new TimeoutEmbeddingProvider(timeoutQuery: false, timeoutBatch: false);
            var udf = new ZoomAndEnhanceUdf(store, provider);
            var chunks = BuildChunksJson(uri, startLine: 1, endLine: 12, score: 0.5);

            var rows = udf.ZoomAndEnhance(chunks, "needle", min_lines: 2, max_depth: 2, threshold: 0.1)
                .OrderByDescending(r => r.Score)
                .ToList();

            rows.Should().HaveCountGreaterThan(0);

            // Chunks from the "needle"-containing half should score higher
            var needleRows = rows.Where(r => r.StartLine >= 7).ToList();
            var plainRows = rows.Where(r => r.EndLine <= 6).ToList();

            needleRows.Should().HaveCountGreaterThan(0);
            plainRows.Should().HaveCountGreaterThan(0);

            var bestNeedle = needleRows.Max(r => r.Score);
            var bestPlain = plainRows.Max(r => r.Score);
            bestNeedle.Should().BeGreaterThan(bestPlain,
                "chunks containing search terms should score higher via lexical boost");
        }
        finally
        {
            ZoomAndEnhanceUdf.ClearCacheForTests();
        }
    }

    [Test]
    public void ZoomAndEnhance_SnapsToEnclosingMethod_WhenReasonablySized()
    {
        ZoomAndEnhanceUdf.ClearCacheForTests();

        try
        {
            using var store = new DuckDbDataStore(":memory:");
            var uri = RepoUri.Parse("file:///test/service.cs")!;

            // 20-line document: lines 1-5 preamble, 6-15 method, 16-20 trailing
            var content = string.Join("\n",
                "using System;",                           // 1
                "namespace Test;",                         // 2
                "public class Service {",                  // 3
                "    private int _count;",                 // 4
                "",                                        // 5
                "    public void DoWork(string input) {",  // 6  ← method start
                "        var needle = input.Trim();",      // 7
                "        if (needle.Length == 0)",          // 8
                "            return;",                     // 9
                "        _count++;",                       // 10
                "        Console.WriteLine(needle);",      // 11
                "        ProcessNeedle(needle);",          // 12
                "        LogResult(_count);",              // 13
                "        SaveState();",                    // 14
                "    }",                                   // 15 ← method end
                "",                                        // 16
                "    private void Other() {",              // 17
                "        // unrelated",                    // 18
                "    }",                                   // 19
                "}");                                      // 20

            var docId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var methodSpanId = Guid.NewGuid();
            var methodNodeId = Guid.NewGuid();

            store.IndexArtifact(uri, new ParsedArtifact
            {
                Artifact = new RepoQL.Contracts.Models.Artifact
                {
                    Id = artifactId,
                    Digest = $"sha256:{Guid.NewGuid():N}",
                    Size = content.Length,
                    MediaType = SemanticMediaType.Parse("text/x-csharp"),
                    Text = content
                },
                DocumentNode = new Node
                {
                    Id = docId,
                    Kind = "document",
                    Uri = uri,
                    ArtifactId = artifactId,
                    Headline = "Service.cs"
                },
                Children =
                [
                    new Node
                    {
                        Id = methodNodeId,
                        Kind = "method",
                        Uri = RepoUri.Parse($"{uri.AbsoluteUri}#symbol=DoWork"),
                        SpanId = methodSpanId,
                        Headline = "DoWork(string input)"
                    }
                ],
                Spans =
                [
                    new RepoQL.Contracts.Models.Span
                    {
                        Id = methodSpanId,
                        DocumentId = docId,
                        StartLine = 6,
                        EndLine = 15
                    }
                ],
                Edges = []
            });

            var provider = new TimeoutEmbeddingProvider(timeoutQuery: false, timeoutBatch: false);
            var udf = new ZoomAndEnhanceUdf(store, provider);

            // BFS chunk starting at lines 7-12 (inside the method, but not covering it fully)
            var chunks = BuildChunksJson(uri, startLine: 7, endLine: 12, score: 0.6);

            var rows = udf.ZoomAndEnhance(chunks, "needle", min_lines: 2, max_depth: 1, threshold: 0.1)
                .ToList();

            rows.Should().HaveCountGreaterThan(0);

            // At least one result should have snapped to the method boundary (6-15)
            var snapped = rows.Where(r => r.StartLine == 6 && r.EndLine == 15).ToList();
            snapped.Should().HaveCountGreaterThan(0,
                "BFS result within a method should snap to the method's line boundaries");
        }
        finally
        {
            ZoomAndEnhanceUdf.ClearCacheForTests();
        }
    }

    [Test]
    public void ZoomAndEnhance_DoesNotSnapToLargeContainer()
    {
        ZoomAndEnhanceUdf.ClearCacheForTests();

        try
        {
            using var store = new DuckDbDataStore(":memory:");
            var uri = RepoUri.Parse("file:///test/big.cs")!;

            // 100-line document with a class spanning lines 1-100
            var lines = new string[100];
            for (var i = 0; i < 100; i++)
                lines[i] = $"// line {i + 1}" + (i == 49 ? " needle target" : "");
            var content = string.Join("\n", lines);

            var docId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var classSpanId = Guid.NewGuid();

            store.IndexArtifact(uri, new ParsedArtifact
            {
                Artifact = new RepoQL.Contracts.Models.Artifact
                {
                    Id = artifactId,
                    Digest = $"sha256:{Guid.NewGuid():N}",
                    Size = content.Length,
                    MediaType = SemanticMediaType.Parse("text/x-csharp"),
                    Text = content
                },
                DocumentNode = new Node
                {
                    Id = docId,
                    Kind = "document",
                    Uri = uri,
                    ArtifactId = artifactId,
                    Headline = "Big.cs"
                },
                Children =
                [
                    new Node
                    {
                        Id = Guid.NewGuid(),
                        Kind = "class",
                        Uri = RepoUri.Parse($"{uri.AbsoluteUri}#symbol=BigClass"),
                        SpanId = classSpanId,
                        Headline = "BigClass"
                    }
                ],
                Spans =
                [
                    new RepoQL.Contracts.Models.Span
                    {
                        Id = classSpanId,
                        DocumentId = docId,
                        StartLine = 1,
                        EndLine = 100
                    }
                ],
                Edges = []
            });

            var provider = new TimeoutEmbeddingProvider(timeoutQuery: false, timeoutBatch: false);
            var udf = new ZoomAndEnhanceUdf(store, provider);

            // Chunk at lines 45-55 (10 lines inside 100-line class → 3x = 30 < 100)
            var chunks = BuildChunksJson(uri, startLine: 45, endLine: 55, score: 0.5);

            var rows = udf.ZoomAndEnhance(chunks, "needle", min_lines: 2, max_depth: 1, threshold: 0.1)
                .ToList();

            rows.Should().HaveCountGreaterThan(0);

            // No result should snap to the full class (lines 1-100)
            var snappedToClass = rows.Where(r => r.StartLine == 1 && r.EndLine == 100).ToList();
            snappedToClass.Should().HaveCount(0,
                "should not snap to a 100-line class when snippet is only ~10 lines");
        }
        finally
        {
            ZoomAndEnhanceUdf.ClearCacheForTests();
        }
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
