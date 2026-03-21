using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Embedding.Storage;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Consolidates embedding shard part files, deduplicates, and evicts expired entries.
/// Complexity: Object storage listing, shard locking via preconditions, DuckDB parquet read/write,
/// deduplication, TTL eviction, safe concurrent access.
/// </summary>
internal sealed class CompactionJob : IDisposable
{
    private readonly WriterSettings _settings;
    private readonly IObjectStorageClient _storageClient;
    private readonly ILogger<CompactionJob> _logger;
    private readonly DuckDBConnection _connection;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private bool _disposed;
    private long _tableSequence;

    public CompactionJob(
        IOptions<WriterSettings> settings,
        IObjectStorageClient storageClient,
        ILogger<CompactionJob> logger)
    {
        _settings = settings.Value;
        _storageClient = storageClient;
        _logger = logger ?? NullLogger<CompactionJob>.Instance;
        _connection = WriterDuckDbUtilities.OpenInMemoryConnection(_settings, enableObjectStorageHttpfs: true);
    }

    public async Task RunNightlyAsync(CancellationToken ct)
    {
        var shards = await DiscoverShardsAsync(ct).ConfigureAwait(false);
        foreach (var shard in shards)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CompactShardAsync(shard, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly compaction failed for shard {ShardPrefix}.", shard.GetPrefix());
            }
        }
    }

    public Task RunShardAsync(CompactionShardInfo shard, CancellationToken ct)
        => CompactShardAsync(shard, ct);

    internal static DateTimeOffset CalculateExpirationCutoff(DateTimeOffset now, TimeSpan ttl)
        => now - ttl;

    private async Task<IReadOnlyList<CompactionShardInfo>> DiscoverShardsAsync(CancellationToken ct)
    {
        var shards = new HashSet<CompactionShardInfo>();

        await foreach (var storageObject in _storageClient.ListObjectsAsync(_settings.EmbeddingsBucket, "source=", ct)
                           .ConfigureAwait(false))
        {
            if (!CompactionShardInfo.TryParse(storageObject.Name, out var shard))
                continue;

            shards.Add(shard);
        }

        return shards
            .OrderBy(static shard => shard.SourceHash, StringComparer.Ordinal)
            .ThenBy(static shard => shard.Model, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task CompactShardAsync(CompactionShardInfo shard, CancellationToken ct)
    {
        var lockAcquired = await TryAcquireLockAsync(shard, ct).ConfigureAwait(false);
        if (!lockAcquired)
            return;

        try
        {
            var partFiles = await ListPartFilesAsync(shard, ct).ConfigureAwait(false);
            if (partFiles.Count == 0)
            {
                _logger.LogDebug("Skipping shard {ShardPrefix} because it has no part files.", shard.GetPrefix());
                return;
            }

            var cleanupCandidates = await ListCleanupCandidatesAsync(shard, ct).ConfigureAwait(false);
            var wroteCompactedShard = await WriteCompactedShardAsync(shard, partFiles, ct).ConfigureAwait(false);
            if (!wroteCompactedShard)
                return;

            await DeleteObsoleteObjectsAsync(partFiles, cleanupCandidates, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseLockAsync(shard, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryAcquireLockAsync(CompactionShardInfo shard, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = CompactionLockFile.Create(WriterDuckDbUtilities.GetInstanceId(), now);
        var lockPath = shard.GetLockPath();

        try
        {
            await UploadLockAsync(lockPath, payload, ObjectStoragePreconditions.DoesNotExist, ct).ConfigureAwait(false);
            return true;
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var existingLock = await TryReadExistingLockAsync(lockPath, ct).ConfigureAwait(false);
            if (existingLock is null)
                return false;

            if (existingLock.Value.LockFile.IsStale(now, _settings.CompactionStaleLockTimeout) == false)
            {
                _logger.LogDebug(
                    "Skipping shard {ShardPrefix} because lock {LockPath} is still fresh.",
                    shard.GetPrefix(),
                    lockPath);
                return false;
            }

            try
            {
                await UploadLockAsync(lockPath, payload, existingLock.Value.Generation, ct).ConfigureAwait(false);
                return true;
            }
            catch (ObjectStorageException staleOverwriteEx)
                when (staleOverwriteEx.StatusCode == HttpStatusCode.PreconditionFailed ||
                      staleOverwriteEx.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    staleOverwriteEx,
                    "Failed to take over stale lock {LockPath}; another compactor won the race.",
                    lockPath);
                return false;
            }
        }
    }

    private async Task<ExistingLockState?> TryReadExistingLockAsync(string lockPath, CancellationToken ct)
    {
        try
        {
            var storageObject = await _storageClient.GetObjectMetadataAsync(
                _settings.EmbeddingsBucket,
                lockPath,
                ct).ConfigureAwait(false);

            await using var stream = new MemoryStream();
            await _storageClient.DownloadAsync(
                _settings.EmbeddingsBucket,
                lockPath,
                stream,
                ct).ConfigureAwait(false);

            stream.Position = 0;

            var lockFile = await JsonSerializer.DeserializeAsync<CompactionLockFile>(
                stream,
                cancellationToken: ct).ConfigureAwait(false);

            if (lockFile is null)
                lockFile = CompactionLockFile.Create("unknown", DateTimeOffset.MinValue);

            return new ExistingLockState(lockFile, storageObject.Generation);
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Compaction lock {LockPath} contained invalid JSON; treating as stale.", lockPath);
            return await TryReadExistingLockMetadataOnlyAsync(lockPath, ct).ConfigureAwait(false);
        }
    }

    private async Task<ExistingLockState?> TryReadExistingLockMetadataOnlyAsync(string lockPath, CancellationToken ct)
    {
        try
        {
            var storageObject = await _storageClient.GetObjectMetadataAsync(
                _settings.EmbeddingsBucket,
                lockPath,
                ct).ConfigureAwait(false);

            return new ExistingLockState(
                CompactionLockFile.Create("unknown", DateTimeOffset.MinValue),
                storageObject.Generation);
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task UploadLockAsync(string lockPath, CompactionLockFile payload, string ifGenerationMatch, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await using var stream = new MemoryStream(bytes, writable: false);
        await _storageClient.UploadWithPreconditionAsync(
            _settings.EmbeddingsBucket,
            lockPath,
            stream,
            ifGenerationMatch,
            ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ObjectStorageObjectInfo>> ListPartFilesAsync(CompactionShardInfo shard, CancellationToken ct)
    {
        var results = new List<ObjectStorageObjectInfo>();

        await foreach (var storageObject in _storageClient.ListObjectsAsync(_settings.EmbeddingsBucket, $"{shard.GetPrefix()}part-", ct)
                           .ConfigureAwait(false))
        {
            if (!IsPartFileName(storageObject.Name))
                continue;

            results.Add(storageObject);
        }

        return results
            .OrderBy(static storageObject => storageObject.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<ObjectStorageObjectInfo>> ListCleanupCandidatesAsync(CompactionShardInfo shard, CancellationToken ct)
    {
        var results = new List<ObjectStorageObjectInfo>();

        await foreach (var storageObject in _storageClient.ListObjectsAsync(_settings.EmbeddingsBucket, shard.GetPrefix(), ct)
                           .ConfigureAwait(false))
        {
            if (!IsPriorCompactedArtifact(storageObject.Name))
                continue;

            results.Add(storageObject);
        }

        return results;
    }

    private async Task<bool> WriteCompactedShardAsync(
        CompactionShardInfo shard,
        IReadOnlyList<ObjectStorageObjectInfo> partFiles,
        CancellationToken ct)
    {
        var tableName = $"embedding_compaction_stage_{Interlocked.Increment(ref _tableSequence)}";

        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var readablePartCount = 0;

                using (var createTable = _connection.CreateCommand())
                {
                    createTable.CommandText = $"""
                        CREATE TEMP TABLE {tableName} (
                            sha256 VARCHAR,
                            vector TINYINT[],
                            created_at TIMESTAMP
                        );
                        """;
                    createTable.ExecuteNonQuery();
                }

                foreach (var partFile in partFiles)
                {
                    try
                    {
                        using var insertCommand = _connection.CreateCommand();
                        insertCommand.CommandText = $"""
                            INSERT INTO {tableName} (sha256, vector, created_at)
                            SELECT sha256, vector, created_at
                            FROM read_parquet('{WriterDuckDbUtilities.EscapeSqlLiteral(
                                WriterDuckDbUtilities.GetObjectStorageUri(_settings, _settings.EmbeddingsBucket, partFile.Name))}');
                            """;
                        insertCommand.ExecuteNonQuery();
                        readablePartCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Skipping corrupted compaction part file {PartFile} in shard {ShardPrefix}.",
                            partFile.Name,
                            shard.GetPrefix());
                    }
                }

                if (readablePartCount == 0)
                {
                    _logger.LogWarning(
                        "Skipping compaction write for shard {ShardPrefix} because every part file was unreadable.",
                        shard.GetPrefix());
                    return false;
                }

                var expirationCutoff = CalculateExpirationCutoff(DateTimeOffset.UtcNow, _settings.CompactionTtl);

                using var copyCommand = _connection.CreateCommand();
                copyCommand.CommandText = $"""
                    COPY (
                        SELECT sha256, vector, created_at
                        FROM (
                            SELECT
                                sha256,
                                vector,
                                created_at,
                                ROW_NUMBER() OVER (PARTITION BY sha256 ORDER BY created_at DESC) AS rn
                            FROM {tableName}
                        ) deduplicated
                        WHERE rn = 1
                          AND created_at >= TIMESTAMP '{WriterDuckDbUtilities.FormatTimestampLiteral(expirationCutoff)}'
                        ORDER BY sha256
                    )
                    TO '{WriterDuckDbUtilities.EscapeSqlLiteral(
                        WriterDuckDbUtilities.GetObjectStorageUri(_settings, _settings.EmbeddingsBucket, shard.GetCompactedPartPath()))}'
                    (FORMAT PARQUET, COMPRESSION zstd, ROW_GROUP_SIZE {_settings.CompactionRowGroupSize}, USE_TMP_FILE false);
                    """;
                copyCommand.ExecuteNonQuery();
                return true;
            }
            finally
            {
                TryDropTempTable(tableName);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task DeleteObsoleteObjectsAsync(
        IReadOnlyList<ObjectStorageObjectInfo> partFiles,
        IReadOnlyList<ObjectStorageObjectInfo> cleanupCandidates,
        CancellationToken ct)
    {
        foreach (var partFile in partFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (partFile.Name is null || string.Equals(Path.GetFileName(partFile.Name), CompactionShardInfo.CompactedPartFileName, StringComparison.Ordinal))
                continue;

            await TryDeleteObjectAsync(partFile.Name, ct).ConfigureAwait(false);
        }

        foreach (var cleanupCandidate in cleanupCandidates)
        {
            ct.ThrowIfCancellationRequested();

            if (cleanupCandidate.Name is null)
                continue;

            await TryDeleteObjectAsync(cleanupCandidate.Name, ct).ConfigureAwait(false);
        }
    }

    private async Task TryDeleteObjectAsync(string objectPath, CancellationToken ct)
    {
        try
        {
            await _storageClient.DeleteAsync(_settings.EmbeddingsBucket, objectPath, ct).ConfigureAwait(false);
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted by a previous attempt.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete obsolete compaction object {ObjectPath}.", objectPath);
        }
    }

    private async Task ReleaseLockAsync(CompactionShardInfo shard, CancellationToken ct)
    {
        try
        {
            await _storageClient.DeleteAsync(_settings.EmbeddingsBucket, shard.GetLockPath(), ct).ConfigureAwait(false);
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Lock already gone.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release compaction lock for shard {ShardPrefix}.", shard.GetPrefix());
        }
    }

    private void TryDropTempTable(string tableName)
    {
        try
        {
            using var dropCommand = _connection.CreateCommand();
            dropCommand.CommandText = $"DROP TABLE IF EXISTS {tableName};";
            dropCommand.ExecuteNonQuery();
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static bool IsPartFileName(string? objectName)
        => !string.IsNullOrWhiteSpace(objectName) &&
           string.Equals(Path.GetExtension(objectName), ".parquet", StringComparison.Ordinal) &&
           Path.GetFileName(objectName).StartsWith("part-", StringComparison.Ordinal);

    private static bool IsPriorCompactedArtifact(string? objectName)
        => !string.IsNullOrWhiteSpace(objectName) &&
           Path.GetFileName(objectName).StartsWith("_compacted-", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connectionGate.Dispose();
        _connection.Dispose();
    }

    private readonly record struct ExistingLockState(CompactionLockFile LockFile, string Generation);
}

/// <summary>
/// Purpose: Identifies a compaction shard from bucket paths or explicit trigger fields.
/// Complexity: Source hash, model name, and derived object paths only.
/// </summary>
internal readonly record struct CompactionShardInfo(string SourceHash, string Model)
{
    internal const string CompactedPartFileName = "part-0001.parquet";

    public static bool TryCreate(string? sourceHash, string? model, out CompactionShardInfo shard)
    {
        shard = default;

        if (string.IsNullOrWhiteSpace(sourceHash) || string.IsNullOrWhiteSpace(model))
            return false;

        shard = new CompactionShardInfo(sourceHash.Trim(), model.Trim());
        return true;
    }

    public static bool TryParse(string? path, out CompactionShardInfo shard)
    {
        shard = default;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalizedPath = path.Trim().Trim('/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return false;

        if (!TryReadPartitionValue(segments[0], "source=", out var sourceHash))
            return false;

        if (!TryReadPartitionValue(segments[1], "model=", out var model))
            return false;

        shard = new CompactionShardInfo(sourceHash, model);
        return true;
    }

    public string GetPrefix() => $"source={SourceHash}/model={Model}/";

    public string GetLockPath() => $"{GetPrefix()}_compaction.lock";

    public string GetCompactedPartPath() => $"{GetPrefix()}{CompactedPartFileName}";

    private static bool TryReadPartitionValue(string segment, string prefix, out string value)
    {
        value = string.Empty;

        if (!segment.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        value = segment[prefix.Length..];
        return !string.IsNullOrWhiteSpace(value);
    }
}

/// <summary>
/// Purpose: Represents the JSON payload stored in a shard compaction lock.
/// Complexity: Instance identity, start timestamp, and stale-lock checks only.
/// </summary>
internal sealed record CompactionLockFile(
    [property: JsonPropertyName("instance")] string Instance,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt)
{
    public static CompactionLockFile Create(string instance, DateTimeOffset startedAt)
        => new(instance, startedAt);

    public bool IsStale(DateTimeOffset now, TimeSpan staleLockTimeout)
        => now - StartedAt >= staleLockTimeout;
}
