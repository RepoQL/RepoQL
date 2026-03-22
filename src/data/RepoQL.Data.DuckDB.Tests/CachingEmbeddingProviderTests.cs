using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB.Tests;

[NotInParallel(nameof(CachingEmbeddingProviderTests))]
public sealed class CachingEmbeddingProviderTests : IDisposable
{
    private readonly string _root;

    public CachingEmbeddingProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "repoql-caching-provider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Test]
    public async Task FullHit_DoesNotCallInnerProvider()
    {
        using var cache = CreateCache(Path.Combine(_root, "full-hit"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);
        var texts = new[] { "alpha", "beta" };

        await SeedPassageEntries(cache, "test-model", texts, text => VectorFor(text, 3));

        var result = await provider.EmbedPassageBatchAsync(texts);

        result.Should().HaveCount(2);
        result[0].Should().Equal(VectorFor("alpha", 3));
        result[1].Should().Equal(VectorFor("beta", 3));
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task FullMiss_CallsInnerAndWritesBack()
    {
        using var cache = CreateCache(Path.Combine(_root, "full-miss"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);
        var texts = new[] { "alpha", "beta" };

        var first = await provider.EmbedPassageBatchAsync(texts);
        var second = await provider.EmbedPassageBatchAsync(texts);

        first[0].Should().Equal(VectorFor("alpha", 3));
        first[1].Should().Equal(VectorFor("beta", 3));
        second[0].Should().Equal(VectorFor("alpha", 3));
        second[1].Should().Equal(VectorFor("beta", 3));

        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task PartialHit_DelegatesOnlyMisses()
    {
        using var cache = CreateCache(Path.Combine(_root, "partial-hit"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);

        await SeedPassageEntries(cache, "test-model", ["cached"], text => VectorFor(text, 3));

        IReadOnlyList<string>? delegated = null;
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .Invokes((IReadOnlyList<string> input, CancellationToken _) => delegated = input)
            .ReturnsLazily((IReadOnlyList<string> input, CancellationToken _) => BuildVectors(input, 3));

        var result = await provider.EmbedPassageBatchAsync(["cached", "miss"]);

        delegated.Should().NotBeNull();
        delegated!.Should().Equal(["miss"]);
        result[0].Should().Equal(VectorFor("cached", 3));
        result[1].Should().Equal(VectorFor("miss", 3));
    }

    [Test]
    public async Task PassageAndQueryKeys_AreIsolated()
    {
        using var cache = CreateCache(Path.Combine(_root, "type-isolation"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);

        var text = "shared-text";
        await provider.EmbedPassageBatchAsync([text]);
        await provider.EmbedQueryBatchAsync([text]);
        await provider.EmbedQueryBatchAsync([text]);

        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => inner.EmbedQueryBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task DisabledCache_PassesThroughToInner()
    {
        using var cache = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = false,
            Path = Path.Combine(_root, "disabled")
        });
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);
        var texts = new[] { "alpha", "beta" };

        var result = await provider.EmbedPassageBatchAsync(texts);

        result.Should().HaveCount(2);
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task WriteFailure_DoesNotFailEmbedding()
    {
        var pathAsFile = Path.Combine(_root, "write-fail-cache");
        File.WriteAllText(pathAsFile, "not-a-directory");

        using var cache = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = pathAsFile
        });
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);

        var result = await provider.EmbedPassageBatchAsync(["alpha"]);

        result[0].Should().Equal(VectorFor("alpha", 3));
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Progress_IsAdjustedToMissCountOnly()
    {
        using var cache = CreateCache(Path.Combine(_root, "miss-progress"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);
        var texts = new[] { "cached", "miss" };
        await SeedPassageEntries(cache, "test-model", ["cached"], text => VectorFor(text, 3));

        BatchEmbeddingProgress? observedProgress = null;
        A.CallTo(() => inner.EmbedPassageBatchAsync(
                A<IReadOnlyList<string>>._,
                A<BatchEmbeddingProgress>._,
                A<CancellationToken>._))
            .Invokes((IReadOnlyList<string> _, BatchEmbeddingProgress progress, CancellationToken _) =>
                observedProgress = progress)
            .ReturnsLazily((IReadOnlyList<string> input, BatchEmbeddingProgress _, CancellationToken _) =>
                BuildVectors(input, 3));

        var progress = new BatchEmbeddingProgress(
            BatchNumber: 2,
            TotalBatches: 5,
            ItemsProcessed: 20,
            TotalItems: 50,
            ElapsedTime: TimeSpan.FromSeconds(5));

        var result = await provider.EmbedPassageBatchAsync(texts, progress);

        observedProgress.Should().NotBeNull();
        observedProgress!.Value.TotalItems.Should().Be(1);
        observedProgress.Value.ItemsProcessed.Should().Be(1);
        result[0].Should().Equal(VectorFor("cached", 3));
        result[1].Should().Equal(VectorFor("miss", 3));
    }

    [Test]
    public async Task SingleItem_EmbedPassageAsync_UsesCacheAndWritesBack()
    {
        using var cache = CreateCache(Path.Combine(_root, "single-passage"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);

        var first = await provider.EmbedPassageAsync("hello");
        var second = await provider.EmbedPassageAsync("hello");

        first.Should().Equal(VectorFor("hello", 3));
        second.Should().Equal(VectorFor("hello", 3));
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task SingleItem_EmbedQueryAsync_UsesCacheAndWritesBack()
    {
        using var cache = CreateCache(Path.Combine(_root, "single-query"));
        var inner = CreateFakeProvider();
        var provider = new CachingEmbeddingProvider(inner, cache);

        var first = await provider.EmbedQueryAsync("hello");
        var second = await provider.EmbedQueryAsync("hello");

        first.Should().Equal(VectorFor("hello", 3));
        second.Should().Equal(VectorFor("hello", 3));
        A.CallTo(() => inner.EmbedQueryBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task NullEmbeddings_FromProvider_AreNotCached()
    {
        using var cache = CreateCache(Path.Combine(_root, "null-embed"));
        var inner = CreateFakeProvider();

        // Override to return null for "bad-text".
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyList<string> input, CancellationToken _) =>
            {
                var results = input.Select(t => t == "bad-text" ? null : (float[]?)VectorFor(t, 3)).ToArray();
                return Task.FromResult(results);
            });

        var provider = new CachingEmbeddingProvider(inner, cache);

        var first = await provider.EmbedPassageBatchAsync(["good-text", "bad-text"]);
        var second = await provider.EmbedPassageBatchAsync(["good-text", "bad-text"]);

        first[0].Should().Equal(VectorFor("good-text", 3));
        first[1].Should().BeNull();

        // good-text: cached (1 call). bad-text: not cached (called again).
        A.CallTo(() => inner.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public void ContentHash_IsDeterministic_AndDistinct()
    {
        var hash1 = CachingEmbeddingProvider.ComputeContentHash("model", "p", "text");
        var hash2 = CachingEmbeddingProvider.ComputeContentHash("model", "p", "text");
        var diffModel = CachingEmbeddingProvider.ComputeContentHash("other-model", "p", "text");
        var diffType = CachingEmbeddingProvider.ComputeContentHash("model", "q", "text");
        var diffText = CachingEmbeddingProvider.ComputeContentHash("model", "p", "other");

        hash1.Should().Be(hash2);
        hash1.Should().NotBe(diffModel);
        hash1.Should().NotBe(diffType);
        hash1.Should().NotBe(diffText);
        hash1.Should().HaveLength(64); // SHA256 = 32 bytes = 64 hex chars
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static IEmbeddingProvider CreateFakeProvider()
    {
        var fake = A.Fake<IEmbeddingProvider>();
        A.CallTo(() => fake.Model).Returns("test-model");
        A.CallTo(() => fake.Dimension).Returns(3);
        A.CallTo(() => fake.Enabled).Returns(true);
        A.CallTo(() => fake.EmbedPassageBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyList<string> input, CancellationToken _) => BuildVectors(input, 3));
        A.CallTo(() => fake.EmbedQueryBatchAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyList<string> input, CancellationToken _) => BuildVectors(input, 3));
        A.CallTo(() => fake.EmbedPassageBatchAsync(
                A<IReadOnlyList<string>>._,
                A<BatchEmbeddingProgress>._,
                A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyList<string> input, BatchEmbeddingProgress _, CancellationToken _) =>
                BuildVectors(input, 3));
        A.CallTo(() => fake.EmbedPassageAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string input, CancellationToken _) => Task.FromResult<float[]?>(VectorFor(input, 3)));
        A.CallTo(() => fake.EmbedQueryAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string input, CancellationToken _) => Task.FromResult<float[]?>(VectorFor(input, 3)));
        return fake;
    }

    private static async Task SeedPassageEntries(
        EmbeddingCache cache,
        string model,
        IReadOnlyList<string> texts,
        Func<string, float[]> vectorFactory)
    {
        var entries = texts.Select(text =>
        {
            var hash = CachingEmbeddingProvider.ComputeContentHash(model, "p", text);
            var vector = vectorFactory(text);
            return new CacheEntry(hash, model, vector.Length, vector, DateTimeOffset.UtcNow);
        }).ToArray();

        await cache.WriteBackAsync(entries);
    }

    private EmbeddingCache CreateCache(string cachePath)
    {
        return new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = cachePath
        });
    }

    private static Task<float[]?[]> BuildVectors(IReadOnlyList<string> input, int dim)
    {
        return Task.FromResult(
            input.Select(text => (float[]?)VectorFor(text, dim)).ToArray());
    }

    private static float[] VectorFor(string text, int dim)
    {
        var vector = new float[dim];
        for (var i = 0; i < dim; i++)
            vector[i] = text.Length + i;
        return vector;
    }
}
