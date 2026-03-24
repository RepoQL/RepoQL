using System.Net.Http.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Embedding.Storage;

namespace RepoQL.Cloud.Service.Embedding.Cache;

/// <summary>
/// Purpose: Intercept chunk embeddings with source-aware cache lookup and asynchronous write-back.
/// Complexity: Owns DuckDB httpfs configuration, object storage reads and writes, dispatch to writer, and int8 vector conversion.
/// </summary>
internal sealed class EmbeddingCacheLayer : IDisposable
{
    private const string DefaultGcsKeyIdEnvVar = "REPOQL_CACHE_GCS_HMAC_KEY_ID";
    private const string DefaultGcsSecretEnvVar = "REPOQL_CACHE_GCS_HMAC_SECRET";

    private readonly CacheLayerSettings _settings;
    private readonly ObjectStorageBackendSettings _storageSettings;
    private readonly string _model;
    private readonly ILogger<EmbeddingCacheLayer> _logger;
    private readonly DuckDBConnection _connection;
    private readonly IObjectStorageClient _storageClient;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private bool _disposed;
    private long _tableSequence;

    public EmbeddingCacheLayer(
        IOptions<CacheLayerSettings> settings,
        IObjectStorageClient storageClient,
        IHttpClientFactory httpClientFactory,
        VoyageAiClient voyage,
        ILogger<EmbeddingCacheLayer> logger)
    {
        _settings = settings.Value;
        _storageSettings = _settings.ToObjectStorageBackendSettings();
        _model = voyage.Model;
        _storageClient = storageClient;
        _logger = logger ?? NullLogger<EmbeddingCacheLayer>.Instance;
        _httpClient = httpClientFactory.CreateClient(nameof(EmbeddingCacheLayer));

#pragma warning disable RQL003
        _connection = new DuckDBConnection("Data Source=:memory:");
#pragma warning restore RQL003
        _connection.Open();
        ConfigureDuckDb();
    }

    public static bool HasRequiredConfiguration(CacheLayerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.EmbeddingsBucket) || string.IsNullOrWhiteSpace(settings.StagingBucket))
            return false;

        try
        {
            settings.ToObjectStorageBackendSettings().Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<CacheLookupResult> LookupAsync(
        string source,
        IReadOnlyList<ChunkFingerprint> fingerprints,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || fingerprints.Count == 0)
            return new CacheLookupResult(new Dictionary<string, byte[]>(StringComparer.Ordinal), fingerprints);

        var uniqueHashes = fingerprints
            .Select(static fingerprint => fingerprint.Sha256)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uniqueHashes.Length == 0)
            return new CacheLookupResult(new Dictionary<string, byte[]>(StringComparer.Ordinal), fingerprints);

        var hits = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sourceHash = EmbeddingCachePrimitives.ComputeSourceHash(source);
        var parquetGlob = GetObjectStorageUri(_settings.EmbeddingsBucket, $"source={sourceHash}/model={_model}/*.parquet");

        try
        {
            await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = BuildLookupSql(parquetGlob, uniqueHashes.Length);
                foreach (var hash in uniqueHashes)
                    command.Parameters.Add(new DuckDBParameter { Value = hash });

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1))
                        continue;

                    var sha256 = reader.GetString(0);
                    var vectorBytes = EmbeddingCachePrimitives.ReadVectorBytes(reader.GetValue(1));
                    if (vectorBytes.Length == 0)
                        continue;

                    hits[sha256] = vectorBytes;
                }
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding cache lookup failed for source hash {SourceHash}. Falling back to Voyage.", sourceHash);
            return new CacheLookupResult(new Dictionary<string, byte[]>(StringComparer.Ordinal), fingerprints);
        }

        var misses = fingerprints
            .Where(fingerprint => !hits.ContainsKey(fingerprint.Sha256))
            .ToList();

        return new CacheLookupResult(hits, misses);
    }

    public Task WriteBackAsync(
        string source,
        IReadOnlyList<CacheEntry> entries,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || entries.Count == 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            string? tempPath = null;

            try
            {
                var validEntries = entries
                    .Where(static entry => !string.IsNullOrWhiteSpace(entry.Sha256) && entry.Vector.Length > 0)
                    .GroupBy(static entry => entry.Sha256, StringComparer.Ordinal)
                    .Select(static group => group.OrderByDescending(entry => entry.CreatedAt).First())
                    .ToArray();
                if (validEntries.Length == 0)
                    return;

                var sourceHash = EmbeddingCachePrimitives.ComputeSourceHash(source);
                var instanceId = GetInstanceId();
                var fileName = $"instance-{instanceId}-{Guid.NewGuid():N}.parquet";
                var objectPath = $"source={sourceHash}/model={_model}/{fileName}";

                tempPath = Path.Combine(Path.GetTempPath(), $"repoql-cache-{Guid.NewGuid():N}.parquet");

                await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    WriteParquet(tempPath, validEntries, ct);
                }
                finally
                {
                    _connectionGate.Release();
                }

                await using (var stream = File.OpenRead(tempPath))
                {
                    _logger.LogDebug(
                        "Writing cache parquet to bucket={Bucket} path={Path} bytes={Bytes}",
                        _settings.StagingBucket, objectPath, stream.Length);
                    await _storageClient.UploadAsync(_settings.StagingBucket, objectPath, stream, ct).ConfigureAwait(false);
                }

                // In production, Eventarc triggers the writer on OBJECT_FINALIZE.
                // In local dev, call the writer directly.
                if (!string.IsNullOrWhiteSpace(_settings.DirectWriterUrl))
                    await NotifyWriterDirectlyAsync(objectPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Embedding cache write-back cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding cache write-back failed.");
            }
            finally
            {
                if (tempPath is not null)
                    TryDeleteTempFile(tempPath);
            }
        }, ct);
    }

    private void ConfigureDuckDb()
    {
        ExecuteNonQuery("INSTALL httpfs;");
        ExecuteNonQuery("LOAD httpfs;");
        ExecuteNonQuery("SET enable_object_cache = true;");

        if (_storageSettings.IsS3())
        {
            var endpoint = new Uri(_storageSettings.S3Endpoint, UriKind.Absolute);
            ExecuteNonQuery($"""
                SET s3_endpoint = '{EscapeSqlLiteral(endpoint.Authority)}';
                SET s3_access_key_id = '{EscapeSqlLiteral(_storageSettings.S3AccessKey)}';
                SET s3_secret_access_key = '{EscapeSqlLiteral(_storageSettings.S3SecretKey)}';
                SET s3_use_ssl = {endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase).ToString().ToLowerInvariant()};
                SET s3_url_style = 'path';
                SET s3_region = 'us-east-1';
                """);
            return;
        }

        // Use S3-compatible settings for GCS. DuckDB's TYPE GCS secret requires the
        // GCS secret provider which may not be registered in all DuckDB.NET.Full builds.
        // The S3-compatible approach via storage.googleapis.com is universally supported.
        var hmacKeyId = GetRequiredEnvironmentValue(DefaultGcsKeyIdEnvVar, "AWS_ACCESS_KEY_ID");
        var hmacSecret = GetRequiredEnvironmentValue(DefaultGcsSecretEnvVar, "AWS_SECRET_ACCESS_KEY");
        ExecuteNonQuery($"""
            SET s3_endpoint = 'storage.googleapis.com';
            SET s3_access_key_id = '{EscapeSqlLiteral(hmacKeyId)}';
            SET s3_secret_access_key = '{EscapeSqlLiteral(hmacSecret)}';
            SET s3_use_ssl = true;
            SET s3_url_style = 'path';
            SET s3_region = 'auto';
            """);
    }

    private void WriteParquet(string tempPath, IReadOnlyList<CacheEntry> entries, CancellationToken ct)
    {
        var tableName = $"embedding_cache_stage_{Interlocked.Increment(ref _tableSequence)}";

        try
        {
            using (var createCommand = _connection.CreateCommand())
            {
                createCommand.CommandText = $"""
                    CREATE TEMP TABLE {tableName} (
                        sha256 VARCHAR,
                        vector TINYINT[],
                        created_at TIMESTAMP
                    );
                    """;
                createCommand.ExecuteNonQuery();
            }

            using var insertCommand = _connection.CreateCommand();
            var values = new string[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                values[i] = "(?, ?, ?)";
                insertCommand.Parameters.Add(new DuckDBParameter { Value = entries[i].Sha256 });
                insertCommand.Parameters.Add(new DuckDBParameter
                {
                    Value = entries[i].Vector.Select(static value => unchecked((sbyte)value)).ToArray()
                });
                insertCommand.Parameters.Add(new DuckDBParameter { Value = entries[i].CreatedAt.UtcDateTime });
            }

            insertCommand.CommandText = $"""
                INSERT INTO {tableName} (sha256, vector, created_at)
                VALUES {string.Join(", ", values)};
                """;
            insertCommand.ExecuteNonQuery();

            using var copyCommand = _connection.CreateCommand();
            copyCommand.CommandText = $"""
                COPY {tableName}
                TO '{EscapeSqlLiteral(tempPath)}'
                (FORMAT PARQUET);
                """;
            copyCommand.ExecuteNonQuery();
        }
        finally
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
    }

    private async Task NotifyWriterDirectlyAsync(string stagingPath, CancellationToken ct)
    {
        using var response = await _httpClient.PostAsJsonAsync(_settings.DirectWriterUrl, new WriteRequest(stagingPath), ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string BuildLookupSql(string parquetGlob, int hashCount)
    {
        var placeholders = string.Join(", ", Enumerable.Repeat("?", hashCount));
        return $"""
            SELECT DISTINCT ON (sha256) sha256, vector
            FROM read_parquet('{EscapeSqlLiteral(parquetGlob)}')
            WHERE sha256 IN ({placeholders})
            ORDER BY sha256, created_at DESC;
            """;
    }

    private static string GetRequiredEnvironmentValue(string primaryName, string fallbackName)
    {
        var value = Environment.GetEnvironmentVariable(primaryName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        value = Environment.GetEnvironmentVariable(fallbackName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Missing required GCS HMAC credential environment variable '{primaryName}' or fallback '{fallbackName}'.");
    }

    private static string GetInstanceId()
    {
        var raw = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(raw))
            raw = Environment.MachineName;

        return raw
            .Trim()
            .Replace('/', '-')
            .Replace('\\', '-');
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private string GetObjectStorageUri(string bucket, string path)
        => $"s3://{bucket}/{path}";

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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connectionGate.Dispose();
        _httpClient.Dispose();
        _connection.Dispose();
    }

    private sealed record WriteRequest(string Path);

    // Note: DirectWriterUrl is only used in local dev (Aspire orchestrator).
    // In production, GCS OBJECT_FINALIZE events trigger the writer via Eventarc.
}
