using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Data.DuckDB.Tests;

[NotInParallel(nameof(EmbeddingCacheTests))]
public sealed class EmbeddingCacheTests : IDisposable
{
    private readonly string _root;
    private readonly string _cachePath;

    public EmbeddingCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "repoql-embedding-cache-tests", Guid.NewGuid().ToString("N"));
        _cachePath = Path.Combine(_root, "cache");
    }

    [Test]
    public async Task WriteBack_ThenLookup_ReturnsCachedEmbedding()
    {
        using var cache = CreateCache();
        var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "alpha");
        var vector = new[] { 0.1f, 0.2f, 0.3f };

        await cache.WriteBackAsync(
        [
            new CacheEntry(hash, "test-model", vector.Length, vector, DateTimeOffset.UtcNow)
        ]);

        var files = Directory.EnumerateFiles(_cachePath, "*.parquet").ToArray();
        files.Should().HaveCount(1);

        var hits = await cache.LookupAsync([hash], "test-model");
        hits.Should().ContainKey(hash);
        hits[hash].MaxDim.Should().Be(vector.Length);
        hits[hash].Embedding.Should().Equal(vector);
    }

    [Test]
    public async Task Lookup_Batch_ReturnsHitsAndSkipsMisses()
    {
        using var cache = CreateCache();
        var hashA = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "alpha");
        var hashB = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "beta");
        var miss = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "missing");

        await cache.WriteBackAsync(
        [
            new CacheEntry(hashA, "test-model", 2, [1.0f, 0.0f], DateTimeOffset.UtcNow),
            new CacheEntry(hashB, "test-model", 2, [0.0f, 1.0f], DateTimeOffset.UtcNow)
        ]);

        var hits = await cache.LookupAsync([hashA, miss, hashB], "test-model");

        hits.Should().ContainKey(hashA);
        hits.Should().ContainKey(hashB);
        hits.Should().NotContainKey(miss);
    }

    [Test]
    public async Task WriteBack_SkipsNullEmbeddings()
    {
        using var cache = CreateCache();
        var hashNull = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "null");
        var hashValid = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "valid");

        await cache.WriteBackAsync(
        [
            new CacheEntry(hashNull, "test-model", 0, null, DateTimeOffset.UtcNow),
            new CacheEntry(hashValid, "test-model", 3, [1.0f, 1.0f, 1.0f], DateTimeOffset.UtcNow)
        ]);

        var hits = await cache.LookupAsync([hashNull, hashValid], "test-model");
        hits.Should().NotContainKey(hashNull);
        hits.Should().ContainKey(hashValid);
    }

    [Test]
    public async Task ConcurrentReadWrite_IsThreadSafe()
    {
        using var cache = CreateCache();

        var workers = Enumerable.Range(0, 8).Select(async worker =>
        {
            for (var i = 0; i < 10; i++)
            {
                var text = $"worker-{worker}-item-{i}";
                var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", text);
                var vector = new[] { worker, i, worker + i }.Select(static v => (float)v).ToArray();

                await cache.WriteBackAsync(
                [
                    new CacheEntry(hash, "test-model", vector.Length, vector, DateTimeOffset.UtcNow)
                ]);

                var hits = await cache.LookupAsync([hash], "test-model");
                hits.Should().ContainKey(hash);
                hits[hash].Embedding.Should().Equal(vector);
            }
        });

        await Task.WhenAll(workers);
    }

    [Test]
    public async Task Compaction_MergesFiles_AndDeduplicates()
    {
        // High threshold prevents auto-compaction during writes.
        using var writeCache = CreateCache(compactionThreshold: 1000, maxSizeMb: 0);

        var duplicateHash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "duplicate");
        var uniqueHashA = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "unique-a");
        var uniqueHashB = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "unique-b");
        var now = DateTimeOffset.UtcNow;

        await writeCache.WriteBackAsync(
        [
            new CacheEntry(duplicateHash, "test-model", 2, [1.0f, 1.0f], now.AddMinutes(-2))
        ]);

        await writeCache.WriteBackAsync(
        [
            new CacheEntry(uniqueHashA, "test-model", 2, [2.0f, 2.0f], now.AddMinutes(-1))
        ]);

        await writeCache.WriteBackAsync(
        [
            new CacheEntry(duplicateHash, "test-model", 2, [9.0f, 9.0f], now),
            new CacheEntry(uniqueHashB, "test-model", 2, [3.0f, 3.0f], now.AddMinutes(1))
        ]);

        Directory.EnumerateFiles(_cachePath, "*.parquet").Should().HaveCount(3);

        // Low threshold allows explicit CompactAsync to proceed.
        using var compactCache = CreateCache(compactionThreshold: 1, maxSizeMb: 0);
        await compactCache.CompactAsync();

        Directory.EnumerateFiles(_cachePath, "*.parquet").Should().HaveCount(1);

        var hits = await compactCache.LookupAsync([duplicateHash, uniqueHashA, uniqueHashB], "test-model");
        hits.Should().HaveCount(3);
        hits[duplicateHash].Embedding.Should().Equal([9.0f, 9.0f]);
        hits[uniqueHashA].Embedding.Should().Equal([2.0f, 2.0f]);
        hits[uniqueHashB].Embedding.Should().Equal([3.0f, 3.0f]);
    }

    [Test]
    public async Task Compaction_EvictsOldest_WhenOverSizeLimit()
    {
        const int totalEntries = 100;
        const int dimensions = 8192;
        var hashes = new List<string>(totalEntries);
        var now = DateTimeOffset.UtcNow;

        using (var writerCache = CreateCache(compactionThreshold: 1000, maxSizeMb: 0))
        {
            for (var batchStart = 0; batchStart < totalEntries; batchStart += 25)
            {
                var batch = new List<CacheEntry>(25);
                for (var i = batchStart; i < Math.Min(totalEntries, batchStart + 25); i++)
                {
                    var text = $"evict-entry-{i}";
                    var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", text);
                    hashes.Add(hash);
                    batch.Add(new CacheEntry(
                        hash,
                        "test-model",
                        dimensions,
                        CreateVector(i, dimensions),
                        now.AddSeconds(i)));
                }

                await writerCache.WriteBackAsync(batch);
            }
        }

        using var compactionCache = CreateCache(compactionThreshold: 1, maxSizeMb: 1);
        await compactionCache.CompactAsync();

        var hits = await compactionCache.LookupAsync(hashes, "test-model");
        hits.Count.Should().BeLessThan(totalEntries);
        hits.Should().NotContainKey(hashes[0]);
        hits.Should().ContainKey(hashes[^1]);
    }

    [Test]
    public async Task Compaction_SkipsWhenLocked()
    {
        // High threshold prevents auto-compaction during writes.
        using var writeCache = CreateCache(compactionThreshold: 1000, maxSizeMb: 0);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", $"locked-{i}");
            await writeCache.WriteBackAsync(
            [
                new CacheEntry(hash, "test-model", 2, [(float)i, i + 1.0f], now.AddSeconds(i))
            ]);
        }

        var beforeFiles = Directory.EnumerateFiles(_cachePath, "*.parquet")
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        File.WriteAllText(GetLockFilePath(), JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        }));

        using var compactCache = CreateCache(compactionThreshold: 1, maxSizeMb: 0);
        await compactCache.CompactAsync();

        var afterFiles = Directory.EnumerateFiles(_cachePath, "*.parquet")
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        afterFiles.Should().BeEquivalentTo(beforeFiles);
    }

    [Test]
    public async Task Compaction_Reclaims_StaleLockFile()
    {
        // High threshold prevents auto-compaction during writes.
        using var writeCache = CreateCache(compactionThreshold: 1000, maxSizeMb: 0);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", $"stale-{i}");
            await writeCache.WriteBackAsync(
            [
                new CacheEntry(hash, "test-model", 2, [(float)i, i + 1.0f], now.AddSeconds(i))
            ]);
        }

        var deadPid = FindDeadPid();
        File.WriteAllText(GetLockFilePath(), JsonSerializer.Serialize(new
        {
            pid = deadPid,
            timestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O")
        }));

        using var compactCache = CreateCache(compactionThreshold: 1, maxSizeMb: 0);
        await compactCache.CompactAsync();

        Directory.EnumerateFiles(_cachePath, "*.parquet").Should().HaveCount(1);
        File.Exists(GetLockFilePath()).Should().BeFalse();
    }

    [Test]
    public async Task Compaction_BelowThreshold_IsNoop()
    {
        using var cache = CreateCache(compactionThreshold: 100, maxSizeMb: 0);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 2; i++)
        {
            var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", $"noop-{i}");
            await cache.WriteBackAsync(
            [
                new CacheEntry(hash, "test-model", 2, [(float)i, i + 1.0f], now.AddSeconds(i))
            ]);
        }

        var beforeFiles = Directory.EnumerateFiles(_cachePath, "*.parquet")
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await cache.CompactAsync();

        var afterFiles = Directory.EnumerateFiles(_cachePath, "*.parquet")
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        afterFiles.Should().BeEquivalentTo(beforeFiles);
    }

    [Test]
    public async Task WriteBack_TriggersCompaction_WhenAboveThreshold()
    {
        using var cache = CreateCache(compactionThreshold: 2, maxSizeMb: 0);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", $"trigger-{i}");
            await cache.WriteBackAsync(
            [
                new CacheEntry(hash, "test-model", 2, [(float)i, i + 1.0f], now.AddSeconds(i))
            ]);
        }

        var compacted = await WaitForConditionAsync(
            () => Directory.Exists(_cachePath) &&
                  Directory.EnumerateFiles(_cachePath, "*.parquet", SearchOption.TopDirectoryOnly).Count() == 1,
            TimeSpan.FromSeconds(5));

        compacted.Should().BeTrue();
    }

    [Test]
    public async Task LayeredLookup_ChecksPathsInOrder()
    {
        var sharedPath = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sharedPath);

        // Seed shared cache with an entry.
        using var sharedCache = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = sharedPath
        });
        var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "shared-text");
        await sharedCache.WriteBackAsync(
        [
            new CacheEntry(hash, "test-model", 3, [1.0f, 2.0f, 3.0f], DateTimeOffset.UtcNow)
        ]);

        // Create layered cache: local (empty) + shared (has the entry).
        using var layered = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Paths = [_cachePath, sharedPath]
        });

        var hits = await layered.LookupAsync([hash], "test-model");

        hits.Should().ContainKey(hash);
        hits[hash].Embedding.Should().Equal([1.0f, 2.0f, 3.0f]);
    }

    [Test]
    public async Task LayeredLookup_LocalHitPreventsSharedCheck()
    {
        var sharedPath = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sharedPath);

        var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "both-text");

        // Seed shared cache with one vector.
        using var sharedCache = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = sharedPath
        });
        await sharedCache.WriteBackAsync(
        [
            new CacheEntry(hash, "test-model", 3, [9.0f, 9.0f, 9.0f], DateTimeOffset.UtcNow)
        ]);

        // Seed local cache with a different vector.
        using var localWriter = CreateCache();
        await localWriter.WriteBackAsync(
        [
            new CacheEntry(hash, "test-model", 3, [1.0f, 1.0f, 1.0f], DateTimeOffset.UtcNow)
        ]);

        // Layered lookup should return local hit, not shared.
        using var layered = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Paths = [_cachePath, sharedPath]
        });

        var hits = await layered.LookupAsync([hash], "test-model");

        hits.Should().ContainKey(hash);
        hits[hash].Embedding.Should().Equal([1.0f, 1.0f, 1.0f]);
    }

    [Test]
    public async Task LayeredLookup_WritesThrough_SharedHitsToLocal()
    {
        var sharedPath = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sharedPath);

        // Seed shared cache.
        using var sharedCache = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = sharedPath
        });
        var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "write-through-text");
        await sharedCache.WriteBackAsync(
        [
            new CacheEntry(hash, "test-model", 3, [5.0f, 6.0f, 7.0f], DateTimeOffset.UtcNow)
        ]);

        // Layered lookup — first call writes through to local.
        using var layered = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Paths = [_cachePath, sharedPath]
        });
        await layered.LookupAsync([hash], "test-model");

        // Local-only cache should now have the entry.
        using var localOnly = CreateCache();
        var localHits = await localOnly.LookupAsync([hash], "test-model");

        localHits.Should().ContainKey(hash);
        localHits[hash].Embedding.Should().Equal([5.0f, 6.0f, 7.0f]);
    }

    [Test]
    public async Task LayeredLookup_SkipsNonexistentSharedPath()
    {
        var missingPath = Path.Combine(_root, "does-not-exist");

        // Seed local cache.
        using var localWriter = CreateCache();
        var hash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "local-only");
        await localWriter.WriteBackAsync(
        [
            new CacheEntry(hash, "test-model", 2, [1.0f, 2.0f], DateTimeOffset.UtcNow)
        ]);

        // Layered with missing shared path should work (skip missing, find in local).
        using var layered = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Paths = [_cachePath, missingPath]
        });

        var hits = await layered.LookupAsync([hash], "test-model");
        hits.Should().ContainKey(hash);
    }

    [Test]
    public async Task LayeredLookup_CombinesHitsAcrossPaths()
    {
        var sharedPath = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sharedPath);

        var localHash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "local-entry");
        var sharedHash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "shared-entry");

        // Seed local.
        using var localWriter = CreateCache();
        await localWriter.WriteBackAsync(
        [
            new CacheEntry(localHash, "test-model", 2, [1.0f, 1.0f], DateTimeOffset.UtcNow)
        ]);

        // Seed shared.
        using var sharedWriter = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = sharedPath
        });
        await sharedWriter.WriteBackAsync(
        [
            new CacheEntry(sharedHash, "test-model", 2, [2.0f, 2.0f], DateTimeOffset.UtcNow)
        ]);

        // Layered lookup should find both.
        using var layered = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Paths = [_cachePath, sharedPath]
        });

        var missHash = CachingEmbeddingProvider.ComputeContentHash("test-model", "p", "miss");
        var hits = await layered.LookupAsync([localHash, sharedHash, missHash], "test-model");

        hits.Should().ContainKey(localHash);
        hits.Should().ContainKey(sharedHash);
        hits.Should().NotContainKey(missHash);
        hits[localHash].Embedding.Should().Equal([1.0f, 1.0f]);
        hits[sharedHash].Embedding.Should().Equal([2.0f, 2.0f]);
    }

    [Test]
    public void ReadPaths_DefaultsToSingleLocalPath()
    {
        using var cache = CreateCache();
        cache.ReadPaths.Should().HaveCount(1);
        cache.ReadPaths[0].Should().Be(cache.CacheDirectory);
    }

    [Test]
    public void ReadPaths_ReflectsConfiguredPaths()
    {
        var sharedPath = Path.Combine(_root, "shared");
        using var cache = new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Paths = [_cachePath, sharedPath]
        });

        cache.ReadPaths.Should().HaveCount(2);
        cache.CacheDirectory.Should().Be(cache.ReadPaths[0]);
    }

    [Test]
    public async Task Lookup_FiltersBy_Model()
    {
        using var cache = CreateCache();
        var text = "shared-text";
        var hashA = CachingEmbeddingProvider.ComputeContentHash("model-a", "p", text);
        var hashB = CachingEmbeddingProvider.ComputeContentHash("model-b", "p", text);

        await cache.WriteBackAsync(
        [
            new CacheEntry(hashA, "model-a", 2, [1.0f, 1.0f], DateTimeOffset.UtcNow),
            new CacheEntry(hashB, "model-b", 2, [2.0f, 2.0f], DateTimeOffset.UtcNow)
        ]);

        var hitsA = await cache.LookupAsync([hashA, hashB], "model-a");
        var hitsB = await cache.LookupAsync([hashA, hashB], "model-b");

        hitsA.Should().ContainKey(hashA);
        hitsA.Should().NotContainKey(hashB);
        hitsB.Should().ContainKey(hashB);
        hitsB.Should().NotContainKey(hashA);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private EmbeddingCache CreateCache(int? compactionThreshold = null, int? maxSizeMb = null, ILogger<EmbeddingCache>? logger = null)
    {
        return new EmbeddingCache(new RepoQlConfig.EmbeddingCacheSettings
        {
            Enabled = true,
            Path = _cachePath,
            CompactionThreshold = compactionThreshold,
            MaxSizeMb = maxSizeMb
        }, logger);
    }

    private string GetLockFilePath()
        => Path.Combine(_cachePath, ".compaction.lock");

    private static float[] CreateVector(int seed, int length)
    {
        var random = new Random(seed);
        var vector = new float[length];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)random.NextDouble();
        return vector;
    }

    private static int FindDeadPid()
    {
        for (var pid = 900_000; pid < 1_200_000; pid++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return pid;
            }
            catch
            {
                return pid;
            }
        }

        return int.MaxValue;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return condition();
    }

}
