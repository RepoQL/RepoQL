using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Persist and retrieve embedding vectors by deterministic content hash across host sessions.
/// Complexity: Owns parquet read/write via a dedicated in-memory DuckDB connection, atomic file writes,
/// layered multi-path resolution with write-through, and concurrency control for safe multi-threaded access.
/// </summary>
public sealed partial class EmbeddingCache : IDisposable
{
    private const string DefaultCachePath = "~/.repoql/embedding-cache/";
    private const int InsertBatchSize = 128;
    private const int DefaultCompactionThreshold = 100;
    private const int DefaultMaxSizeMb = 500;
    private const string CompactionLockFileName = ".compaction.lock";

    private readonly RepoQlConfig.EmbeddingCacheSettings _settings;
    private readonly ILogger<EmbeddingCache> _logger;
    private readonly DuckDBConnection _connection;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private static long s_fileSequence;
    private long _tableSequence;
    private bool _disposed;

    public EmbeddingCache(
        RepoQlConfig.EmbeddingCacheSettings? settings = null,
        ILogger<EmbeddingCache>? logger = null)
    {
        _settings = settings ?? new RepoQlConfig.EmbeddingCacheSettings();
        _logger = logger ?? NullLogger<EmbeddingCache>.Instance;
        Enabled = _settings.Enabled ?? true;

        var resolvedPaths = ResolvePaths(_settings);
        CacheDirectory = resolvedPaths[0];
        ReadPaths = resolvedPaths;

        _connection = new DuckDBConnection("Data Source=:memory:");
        _connection.Open();
    }

    public bool Enabled { get; }

    /// <summary>The local (first) path — write target for new embeddings and compaction.</summary>
    public string CacheDirectory { get; }

    /// <summary>All resolved paths in priority order. First is local (read-write), rest are shared (read-only).</summary>
    public IReadOnlyList<string> ReadPaths { get; }

    public async Task<Dictionary<string, CachedEmbedding>> LookupAsync(
        IReadOnlyList<string> contentHashes,
        string model,
        CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(model) || contentHashes.Count == 0)
            return new Dictionary<string, CachedEmbedding>(StringComparer.Ordinal);

        var pending = new HashSet<string>(
            contentHashes.Where(static hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.Ordinal);
        if (pending.Count == 0)
            return new Dictionary<string, CachedEmbedding>(StringComparer.Ordinal);

        var hits = new Dictionary<string, CachedEmbedding>(StringComparer.Ordinal);
        List<CacheEntry>? writeThrough = null;

        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var pathIndex = 0; pathIndex < ReadPaths.Count && pending.Count > 0; pathIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var cachePath = ReadPaths[pathIndex];
                var isLocal = pathIndex == 0;

                if (!TryEnumerateParquetFiles(cachePath, out var parquetFiles))
                    continue;

                // Snapshot pending before this path so we can identify new hits.
                HashSet<string>? pendingBefore = !isLocal ? new HashSet<string>(pending, StringComparer.Ordinal) : null;

                foreach (var parquetFile in parquetFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pending.Count == 0)
                        break;

                    TryReadSingleParquet(parquetFile, model, pending, hits);
                }

                // Write-through: hits from shared paths get cached locally.
                if (pendingBefore is not null && pending.Count < pendingBefore.Count)
                {
                    writeThrough ??= [];
                    foreach (var hash in pendingBefore)
                    {
                        if (!pending.Contains(hash) && hits.TryGetValue(hash, out var cached))
                        {
                            writeThrough.Add(new CacheEntry(
                                hash, model, cached.MaxDim, cached.Embedding, DateTimeOffset.UtcNow));
                        }
                    }
                }
            }
        }
        finally
        {
            _connectionGate.Release();
        }

        // Write-through outside the connection gate to avoid holding it during I/O.
        if (writeThrough is { Count: > 0 })
        {
            try
            {
                await WriteBackAsync(writeThrough, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Write-through to local cache failed. Shared cache hits are still returned.");
            }
        }

        return hits;
    }

    public async Task WriteBackAsync(IReadOnlyList<CacheEntry> entries, CancellationToken ct = default)
    {
        if (!Enabled || entries.Count == 0)
            return;

        var validEntries = entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.TextHash) &&
                                   !string.IsNullOrWhiteSpace(entry.Model) &&
                                   entry.Embedding is { Length: > 0 })
            .ToArray();
        if (validEntries.Length == 0)
            return;

        string? tempPath = null;

        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var fileName = BuildFileName();
            tempPath = Path.Combine(CacheDirectory, ".tmp-" + fileName);
            var finalPath = Path.Combine(CacheDirectory, fileName);

            await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                WriteParquet(tempPath, validEntries, ct);
            }
            finally
            {
                _connectionGate.Release();
            }

            File.Move(tempPath, finalPath, overwrite: false);
            TriggerCompactionIfNeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding cache write-back failed. Continuing without cache.");
            if (tempPath is not null)
                TryDeleteTempFile(tempPath);
        }
    }

    public void TriggerStartupCompaction()
    {
        if (!Enabled || _disposed)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await CompactAsync(_disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cache is being disposed — expected.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding cache startup compaction failed.");
            }
        });
    }

    public async Task CompactAsync(CancellationToken ct = default)
    {
        if (!Enabled || _disposed)
            return;

        var threshold = _settings.CompactionThreshold ?? DefaultCompactionThreshold;
        if (threshold < 0)
            threshold = 0;

        try
        {
            if (!Directory.Exists(CacheDirectory))
                return;

            var parquetFiles = EnumerateParquetFiles();
            if (parquetFiles.Length <= threshold)
                return;

            if (!TryAcquireLockFile())
                return;

            try
            {
                parquetFiles = EnumerateParquetFiles();
                if (parquetFiles.Length <= threshold)
                    return;

                await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var mergedEntries = ReadAndDeduplicateEntries(parquetFiles, ct);
                    if (mergedEntries.Count == 0)
                        return;

                    var maxSizeMb = _settings.MaxSizeMb ?? DefaultMaxSizeMb;
                    var maxSizeBytes = maxSizeMb > 0 ? maxSizeMb * 1024L * 1024L : long.MaxValue;

                    var fileName = BuildFileName();
                    var tempPath = Path.Combine(CacheDirectory, ".tmp-" + fileName);
                    var finalPath = Path.Combine(CacheDirectory, fileName);
                    var moved = false;

                    try
                    {
                        var (evictedCount, cacheSizeBytes) =
                            WriteMergedFileWithEviction(tempPath, mergedEntries, maxSizeBytes, ct);

                        File.Move(tempPath, finalPath, overwrite: false);
                        moved = true;

                        var deletedFiles = 0;
                        foreach (var sourceFile in parquetFiles)
                        {
                            try
                            {
                                if (File.Exists(sourceFile))
                                {
                                    File.Delete(sourceFile);
                                    deletedFiles++;
                                }
                            }
                            catch (IOException)
                            {
                                // Best effort cleanup; open handles are expected on Windows.
                            }
                            catch (UnauthorizedAccessException)
                            {
                                // Best effort cleanup.
                            }
                        }

                        _logger.LogInformation(
                            "Embedding cache compaction completed. Source files={SourceCount}, deleted={DeletedCount}, evicted entries={EvictedCount}, resulting size={SizeBytes} bytes.",
                            parquetFiles.Length,
                            deletedFiles,
                            evictedCount,
                            cacheSizeBytes);
                    }
                    finally
                    {
                        if (!moved)
                            TryDeleteTempFile(tempPath);
                    }
                }
                finally
                {
                    _connectionGate.Release();
                }
            }
            finally
            {
                ReleaseLockFile();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Embedding cache compaction canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding cache compaction failed. Continuing without cache maintenance.");
        }
    }

    private List<CacheEntry> ReadAndDeduplicateEntries(IReadOnlyList<string> parquetFiles, CancellationToken ct)
    {
        var sourceTableName = $"embedding_cache_compact_source_{Interlocked.Increment(ref _tableSequence)}";
        var dedupTableName = $"embedding_cache_compact_dedup_{Interlocked.Increment(ref _tableSequence)}";
        var missingFileCount = 0;
        var unreadableFileCount = 0;
        List<string>? unreadableSamples = null;

        try
        {
            using (var createSource = _connection.CreateCommand())
            {
                createSource.CommandText = $"""
                    CREATE TEMP TABLE {sourceTableName} (
                        text_hash VARCHAR,
                        model VARCHAR,
                        max_dim INTEGER,
                        embedding FLOAT[],
                        created_at TIMESTAMP
                    );
                    """;
                createSource.ExecuteNonQuery();
            }

            foreach (var parquetFile in parquetFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(parquetFile))
                {
                    missingFileCount++;
                    continue;
                }

                try
                {
                    using var importCmd = _connection.CreateCommand();
                    importCmd.CommandText = $"""
                        INSERT INTO {sourceTableName} (text_hash, model, max_dim, embedding, created_at)
                        SELECT text_hash, model, max_dim, embedding, created_at
                        FROM read_parquet('{EscapeSqlLiteral(parquetFile)}');
                        """;
                    importCmd.ExecuteNonQuery();
                }
                catch (Exception ex) when (IsMissingParquetFileException(parquetFile, ex))
                {
                    missingFileCount++;
                }
                catch (Exception ex)
                {
                    unreadableFileCount++;
                    unreadableSamples ??= [];
                    if (unreadableSamples.Count < 3)
                        unreadableSamples.Add(parquetFile);

                    _logger.LogDebug(ex, "Embedding cache compaction could not read parquet file {File}.", parquetFile);
                }
            }

            if (missingFileCount > 0)
            {
                _logger.LogDebug(
                    "Embedding cache compaction skipped {Count} parquet files that disappeared before they could be read.",
                    missingFileCount);
            }

            if (unreadableFileCount > 0)
            {
                _logger.LogWarning(
                    "Embedding cache compaction skipped {Count} unreadable parquet files. Sample: {Files}",
                    unreadableFileCount,
                    string.Join(", ", unreadableSamples ?? []));
            }

            using (var dedupCmd = _connection.CreateCommand())
            {
                dedupCmd.CommandText = $"""
                    CREATE TEMP TABLE {dedupTableName} AS
                    SELECT text_hash, model, max_dim, embedding, created_at
                    FROM (
                        SELECT
                            text_hash,
                            model,
                            max_dim,
                            embedding,
                            created_at,
                            ROW_NUMBER() OVER (
                                PARTITION BY text_hash
                                ORDER BY created_at DESC NULLS LAST
                            ) AS row_rank
                        FROM {sourceTableName}
                    )
                    WHERE row_rank = 1;
                    """;
                dedupCmd.ExecuteNonQuery();
            }

            using var readCmd = _connection.CreateCommand();
            readCmd.CommandText = $"""
                SELECT text_hash, model, max_dim, embedding, created_at
                FROM {dedupTableName}
                ORDER BY created_at ASC NULLS FIRST, text_hash ASC;
                """;

            var entries = new List<CacheEntry>();
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;

                var hash = reader.GetString(0);
                var model = reader.GetString(1);
                var embedding = ReadEmbedding(reader.GetValue(3));
                if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(model) || embedding is null || embedding.Length == 0)
                    continue;

                var maxDim = reader.IsDBNull(2)
                    ? embedding.Length
                    : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                var createdAt = reader.IsDBNull(4)
                    ? DateTimeOffset.UtcNow
                    : ReadCreatedAt(reader.GetValue(4));

                entries.Add(new CacheEntry(hash, model, maxDim, embedding, createdAt));
            }

            return entries;
        }
        finally
        {
            try
            {
                using var dropSource = _connection.CreateCommand();
                dropSource.CommandText = $"DROP TABLE IF EXISTS {sourceTableName};";
                dropSource.ExecuteNonQuery();
            }
            catch
            {
                // Best effort cleanup.
            }

            try
            {
                using var dropDedup = _connection.CreateCommand();
                dropDedup.CommandText = $"DROP TABLE IF EXISTS {dedupTableName};";
                dropDedup.ExecuteNonQuery();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private (int EvictedCount, long SizeBytes) WriteMergedFileWithEviction(
        string tempPath,
        IReadOnlyList<CacheEntry> mergedEntries,
        long maxSizeBytes,
        CancellationToken ct)
    {
        // Sort oldest-first so eviction trims from the front.
        var working = mergedEntries
            .OrderBy(static entry => entry.CreatedAt)
            .ThenBy(static entry => entry.TextHash, StringComparer.Ordinal)
            .ToList();

        // First write: full dataset to measure compressed size.
        ct.ThrowIfCancellationRequested();
        WriteParquet(tempPath, working, ct);
        var sizeBytes = new FileInfo(tempPath).Length;

        if (maxSizeBytes == long.MaxValue || sizeBytes <= maxSizeBytes || working.Count == 0)
            return (0, sizeBytes);

        // Estimate how many entries to drop based on measured per-row size.
        var avgBytesPerRow = Math.Max(1d, sizeBytes / (double)working.Count);
        var bytesToTrim = sizeBytes - maxSizeBytes;
        var evictedCount = Math.Clamp((int)Math.Ceiling(bytesToTrim / avgBytesPerRow), 1, working.Count);
        working.RemoveRange(0, evictedCount);

        if (working.Count == 0)
        {
            TryDeleteTempFile(tempPath);
            return (evictedCount, 0);
        }

        // Second write: trimmed dataset.
        ct.ThrowIfCancellationRequested();
        TryDeleteTempFile(tempPath);
        WriteParquet(tempPath, working, ct);
        sizeBytes = new FileInfo(tempPath).Length;

        return (evictedCount, sizeBytes);
    }

    private void TriggerCompactionIfNeeded()
    {
        if (!Enabled || _disposed)
            return;

        try
        {
            if (!Directory.Exists(CacheDirectory))
                return;

            var threshold = _settings.CompactionThreshold ?? DefaultCompactionThreshold;
            if (threshold < 0)
                threshold = 0;

            var fileCount = Directory.EnumerateFiles(CacheDirectory, "*.parquet", SearchOption.TopDirectoryOnly)
                .Take(threshold + 1)
                .Count();
            if (fileCount <= threshold)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await CompactAsync(_disposeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cache is being disposed — expected.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Embedding cache background compaction failed.");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding cache compaction trigger check failed.");
        }
    }

    private bool TryAcquireLockFile()
    {
        var lockPath = GetLockFilePath();

        try
        {
            Directory.CreateDirectory(CacheDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding cache lock directory creation failed.");
            return false;
        }

        try
        {
            if (File.Exists(lockPath))
            {
                var existingPid = TryReadLockFilePid(lockPath);
                if (existingPid is int pid && IsProcessAlive(pid))
                    return false;

                try
                {
                    File.Delete(lockPath);
                }
                catch
                {
                    return false;
                }
            }

            var payload = JsonSerializer.Serialize(new LockFilePayload
            {
                pid = Environment.ProcessId,
                timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }, LockFileJsonContext.Default.LockFilePayload);

            using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(payload);
            writer.Flush();
            stream.Flush(true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding cache lockfile acquisition failed.");
            return false;
        }
    }

    private void ReleaseLockFile()
    {
        var lockPath = GetLockFilePath();

        try
        {
            if (!File.Exists(lockPath))
                return;

            var pid = TryReadLockFilePid(lockPath);
            if (pid is int ownerPid && ownerPid != Environment.ProcessId)
                return;

            File.Delete(lockPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding cache lockfile release failed.");
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private string[] EnumerateParquetFiles()
        => TryEnumerateParquetFiles(CacheDirectory, out var files) ? files : [];

    private bool TryEnumerateParquetFiles(string cachePath, out string[] files)
    {
        files = [];

        try
        {
            if (!Directory.Exists(cachePath))
                return false;

            files = Directory.EnumerateFiles(cachePath, "*.parquet", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.Length > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache path {Path} is unreachable. Skipping.", cachePath);
            return false;
        }
    }

    private string GetLockFilePath()
        => Path.Combine(CacheDirectory, CompactionLockFileName);

    private static int? TryReadLockFilePid(string lockPath)
    {
        try
        {
            var text = File.ReadAllText(lockPath);
            var payload = JsonSerializer.Deserialize(text, LockFileJsonContext.Default.LockFilePayload);
            return payload is { pid: > 0 } ? payload.pid : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset ReadCreatedAt(object value)
    {
        if (value is DateTimeOffset dto)
            return dto.ToUniversalTime();

        if (value is DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            return new DateTimeOffset(dt.ToUniversalTime());
        }

        return DateTimeOffset.UtcNow;
    }

    private void TryReadSingleParquet(
        string parquetFile,
        string model,
        HashSet<string> pending,
        Dictionary<string, CachedEmbedding> hits)
    {
        try
        {
            var requestedHashes = pending.ToArray();
            if (requestedHashes.Length == 0)
                return;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = BuildLookupSql(parquetFile, requestedHashes.Length);
            cmd.Parameters.Add(new DuckDBParameter { Value = model });
            foreach (var hash in requestedHashes)
                cmd.Parameters.Add(new DuckDBParameter { Value = hash });

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    continue;

                var hash = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(hash) || !pending.Contains(hash))
                    continue;

                var embedding = ReadEmbedding(reader.GetValue(1));
                if (embedding is null || embedding.Length == 0)
                    continue;

                var maxDim = reader.IsDBNull(2)
                    ? embedding.Length
                    : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);

                hits[hash] = new CachedEmbedding(embedding, maxDim);
                pending.Remove(hash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding cache read failed for parquet file {File}. Skipping file.", parquetFile);
        }
    }

    private void WriteParquet(string tempPath, IReadOnlyList<CacheEntry> entries, CancellationToken ct)
    {
        var tableName = $"embedding_cache_write_{Interlocked.Increment(ref _tableSequence)}";

        try
        {
            using (var createCmd = _connection.CreateCommand())
            {
                createCmd.CommandText = $"""
                    CREATE TEMP TABLE {tableName} (
                        text_hash VARCHAR,
                        model VARCHAR,
                        max_dim INTEGER,
                        embedding FLOAT[],
                        created_at TIMESTAMP
                    );
                    """;
                createCmd.ExecuteNonQuery();
            }

            using var insertCmd = _connection.CreateCommand();
            for (var offset = 0; offset < entries.Count; offset += InsertBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                insertCmd.Parameters.Clear();
                var batchCount = Math.Min(InsertBatchSize, entries.Count - offset);

                var valuePlaceholders = new string[batchCount];
                for (var i = 0; i < batchCount; i++)
                {
                    var entry = entries[offset + i];
                    var embedding = entry.Embedding!;
                    var createdAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt;

                    valuePlaceholders[i] = "(?, ?, ?, ?, ?)";
                    insertCmd.Parameters.Add(new DuckDBParameter { Value = entry.TextHash });
                    insertCmd.Parameters.Add(new DuckDBParameter { Value = entry.Model });
                    insertCmd.Parameters.Add(new DuckDBParameter { Value = entry.MaxDim > 0 ? entry.MaxDim : embedding.Length });
                    insertCmd.Parameters.Add(new DuckDBParameter { Value = new List<float>(embedding) });
                    insertCmd.Parameters.Add(new DuckDBParameter { Value = createdAt.UtcDateTime });
                }

                insertCmd.CommandText = $"""
                    INSERT INTO {tableName} (text_hash, model, max_dim, embedding, created_at)
                    VALUES {string.Join(", ", valuePlaceholders)};
                    """;
                insertCmd.ExecuteNonQuery();
            }

            using var copyCmd = _connection.CreateCommand();
            copyCmd.CommandText = $"""
                COPY {tableName}
                TO '{EscapeSqlLiteral(tempPath)}'
                (FORMAT PARQUET);
                """;
            copyCmd.ExecuteNonQuery();
        }
        finally
        {
            try
            {
                using var dropCmd = _connection.CreateCommand();
                dropCmd.CommandText = $"DROP TABLE IF EXISTS {tableName};";
                dropCmd.ExecuteNonQuery();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static string BuildFileName()
    {
        var seq = Interlocked.Increment(ref s_fileSequence);
        return $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{seq}.parquet";
    }

    private static string BuildLookupSql(string parquetFile, int hashCount)
    {
        var placeholders = string.Join(", ", Enumerable.Repeat("?", hashCount));
        return $"""
            SELECT text_hash, embedding, max_dim
            FROM read_parquet('{EscapeSqlLiteral(parquetFile)}')
            WHERE model = ? AND text_hash IN ({placeholders});
            """;
    }

    private static IReadOnlyList<string> ResolvePaths(RepoQlConfig.EmbeddingCacheSettings settings)
    {
        // Paths takes precedence over Path. Fall back to default if both empty.
        if (settings.Paths is { Count: > 0 })
        {
            var resolved = settings.Paths
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .Select(static p => ResolveSinglePath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (resolved.Length > 0)
                return resolved;
        }

        return [ResolveSinglePath(settings.Path)];
    }

    private static string ResolveSinglePath(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? DefaultCachePath : configuredPath.Trim();

        // Expand environment variables (%USERPROFILE%, $HOME, etc.)
        path = Environment.ExpandEnvironmentVariables(path);

        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, path[2..]));
        }

        return Path.GetFullPath(path);
    }

    private static float[]? ReadEmbedding(object? value)
    {
        return value switch
        {
            null => null,
            float[] vec => vec,
            List<float> list => [.. list],
            IEnumerable<float> enumerable => [.. enumerable],
            _ => null
        };
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    internal static bool IsMissingParquetFileException(string parquetFile, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parquetFile);
        ArgumentNullException.ThrowIfNull(exception);

        if (!File.Exists(parquetFile))
            return true;

        return exception.Message.Contains("No files found that match the pattern", StringComparison.OrdinalIgnoreCase)
               && exception.Message.Contains(parquetFile, StringComparison.Ordinal);
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _connectionGate.Dispose();
        _connection.Dispose();
    }

    private sealed class LockFilePayload
    {
        public int pid { get; set; }
        public string? timestamp { get; set; }
    }

    [JsonSerializable(typeof(LockFilePayload))]
    private sealed partial class LockFileJsonContext : JsonSerializerContext;
}

/// <summary>
/// Purpose: Represent a cache hit containing the embedding vector and the stored model dimensionality.
/// Complexity: Immutable transport record for lookup results.
/// </summary>
public readonly record struct CachedEmbedding(float[] Embedding, int MaxDim);

/// <summary>
/// Purpose: Represent a cache write candidate produced by embedding computation.
/// Complexity: Immutable payload record used to stage parquet write-back batches.
/// </summary>
public sealed record CacheEntry(
    string TextHash,
    string Model,
    int MaxDim,
    float[]? Embedding,
    DateTimeOffset CreatedAt);
