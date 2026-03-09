using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DuckDB.NET.Data;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Embedding.Storage;
using CloudTask = Google.Cloud.Tasks.V2.Task;
using CloudTaskHttpMethod = Google.Cloud.Tasks.V2.HttpMethod;
using CloudTaskHttpRequest = Google.Cloud.Tasks.V2.HttpRequest;
using CloudTasksClient = Google.Cloud.Tasks.V2.CloudTasksClient;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Processes staging files into permanent embeddings shards.
/// Complexity: Object storage read/write, parquet I/O via DuckDB, shard path extraction,
/// _source.json creation, compaction threshold check.
/// </summary>
internal sealed class CacheMergeHandler : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.Embedding.Writer");
    private const string SourceMetadataFileName = "_source.json";

    private readonly WriterSettings _settings;
    private readonly IObjectStorageClient _storageClient;
    private readonly CloudTasksClient? _tasksClient;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CacheMergeHandler> _logger;
    private readonly DuckDBConnection _connection;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private bool _disposed;
    private long _tableSequence;
    private long _lastIssuedPartTimestamp;

    public CacheMergeHandler(
        IOptions<WriterSettings> settings,
        IObjectStorageClient storageClient,
        IHttpClientFactory httpClientFactory,
        ILogger<CacheMergeHandler> logger)
    {
        _settings = settings.Value;
        _storageClient = storageClient;
        _httpClient = httpClientFactory.CreateClient(nameof(CacheMergeHandler));
        _logger = logger ?? NullLogger<CacheMergeHandler>.Instance;

        _connection = WriterDuckDbUtilities.OpenInMemoryConnection(_settings, enableObjectStorageHttpfs: false);

        if (string.IsNullOrWhiteSpace(_settings.DirectCompactionUrl) &&
            !string.IsNullOrWhiteSpace(_settings.CompactionQueue) &&
            !string.IsNullOrWhiteSpace(_settings.CompactionEndpointUrl))
        {
            _tasksClient = CloudTasksClient.Create();
        }
    }

    public static bool TryParseStagingPath(string? stagingPath, out StagingPathInfo pathInfo)
    {
        pathInfo = default;

        if (string.IsNullOrWhiteSpace(stagingPath))
            return false;

        var normalizedPath = stagingPath.Trim().Trim('/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 3)
            return false;

        if (!TryReadPartitionValue(segments[0], "source=", out var sourceHash))
            return false;

        if (!TryReadPartitionValue(segments[1], "model=", out var model))
            return false;

        var fileName = segments[2];
        if (!fileName.StartsWith("instance-", StringComparison.Ordinal) ||
            !fileName.EndsWith(".parquet", StringComparison.Ordinal) ||
            fileName.Length <= "instance-".Length + ".parquet".Length)
        {
            return false;
        }

        pathInfo = new StagingPathInfo(normalizedPath, sourceHash, model);
        return true;
    }

    public async Task HandleAsync(string stagingPath, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("writer.merge", ActivityKind.Consumer);

        if (!TryParseStagingPath(stagingPath, out var pathInfo))
        {
            _logger.LogWarning("Ignoring merge request with invalid staging path {StagingPath}", stagingPath);
            activity?.SetTag("merge.status", "invalid_path");
            return;
        }

        activity?.SetTag("merge.source_hash", pathInfo.SourceHash);
        activity?.SetTag("merge.model", pathInfo.Model);

        string? downloadedStagingFile = null;
        string? mergedPartFile = null;

        try
        {
            downloadedStagingFile = Path.Combine(Path.GetTempPath(), $"repoql-embedding-staging-{Guid.NewGuid():N}.parquet");
            mergedPartFile = Path.Combine(Path.GetTempPath(), $"repoql-embedding-part-{Guid.NewGuid():N}.parquet");

            var stagingExists = await TryDownloadStagingFileAsync(pathInfo.Path, downloadedStagingFile, ct).ConfigureAwait(false);
            if (!stagingExists)
                return;

            var parquetIsReadable = await TryCreateSortedPartFileAsync(downloadedStagingFile, mergedPartFile, ct).ConfigureAwait(false);
            if (!parquetIsReadable)
                return;

            var shardPrefix = pathInfo.GetShardPrefix();
            var hadExistingParts = await TryHasPartFilesAsync(shardPrefix, ct).ConfigureAwait(false);
            var partPath = $"{shardPrefix}part-{GetNextPartTimestampMilliseconds()}.parquet";

            await UploadPartFileAsync(partPath, mergedPartFile, ct).ConfigureAwait(false);

            if (hadExistingParts == false)
                await TryWriteSourceMetadataAsync(shardPrefix, ct).ConfigureAwait(false);

            await TryDeleteStagingFileAsync(pathInfo.Path, ct).ConfigureAwait(false);

            var partCount = await TryCountPartFilesAsync(shardPrefix, ct).ConfigureAwait(false);
            if (partCount is > 0 && partCount > _settings.PartCountThreshold)
                await TryEnqueueCompactionAsync(pathInfo, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTempFile(downloadedStagingFile);
            TryDeleteTempFile(mergedPartFile);
        }
    }

    private async Task<bool> TryDownloadStagingFileAsync(string stagingPath, string localPath, CancellationToken ct)
    {
        try
        {
            await using var destination = File.Create(localPath);
            await _storageClient.DownloadAsync(_settings.StagingBucket, stagingPath, destination, ct).ConfigureAwait(false);
            return true;
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation(
                "Staging parquet {StagingPath} was missing from bucket {Bucket}; acknowledging without retry.",
                stagingPath,
                _settings.StagingBucket);
            return false;
        }
        catch
        {
            TryDeleteTempFile(localPath);
            throw;
        }
    }

    private async Task<bool> TryCreateSortedPartFileAsync(string sourceFile, string destinationFile, CancellationToken ct)
    {
        var tempTableName = $"embedding_writer_stage_{Interlocked.Increment(ref _tableSequence)}";

        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                using var createTable = _connection.CreateCommand();
                createTable.CommandText = $"""
                    CREATE TEMP TABLE {tempTableName} AS
                    SELECT sha256, vector, created_at
                    FROM read_parquet('{WriterDuckDbUtilities.EscapeSqlLiteral(sourceFile)}')
                    ORDER BY sha256;
                    """;
                createTable.ExecuteNonQuery();

                using var copyCommand = _connection.CreateCommand();
                copyCommand.CommandText = $"""
                    COPY {tempTableName}
                    TO '{WriterDuckDbUtilities.EscapeSqlLiteral(destinationFile)}'
                    (FORMAT PARQUET, COMPRESSION zstd);
                    """;
                copyCommand.ExecuteNonQuery();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staging parquet {StagingFile} was corrupt or unreadable; acknowledging without retry.", sourceFile);
                return false;
            }
            finally
            {
                TryDropTempTable(tempTableName);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task UploadPartFileAsync(string objectPath, string localPath, CancellationToken ct)
    {
        await using var source = File.OpenRead(localPath);
        await _storageClient.UploadAsync(_settings.EmbeddingsBucket, objectPath, source, ct).ConfigureAwait(false);
    }

    private async Task<bool?> TryHasPartFilesAsync(string shardPrefix, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _storageClient.ListObjectsAsync(_settings.EmbeddingsBucket, $"{shardPrefix}part-", ct)
                               .ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect shard {ShardPrefix} before writing.", shardPrefix);
            return null;
        }
    }

    private async Task<int?> TryCountPartFilesAsync(string shardPrefix, CancellationToken ct)
    {
        try
        {
            var count = 0;
            await foreach (var _ in _storageClient.ListObjectsAsync(_settings.EmbeddingsBucket, $"{shardPrefix}part-", ct)
                               .ConfigureAwait(false))
            {
                count++;
            }

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to count part files in shard {ShardPrefix}.", shardPrefix);
            return null;
        }
    }

    private async Task TryWriteSourceMetadataAsync(string shardPrefix, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new SourceMetadata("unknown"));
            await using var stream = new MemoryStream(payload, writable: false);

            await _storageClient.UploadAsync(
                _settings.EmbeddingsBucket,
                $"{shardPrefix}{SourceMetadataFileName}",
                stream,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write shard metadata for {ShardPrefix}.", shardPrefix);
        }
    }

    private async Task TryDeleteStagingFileAsync(string stagingPath, CancellationToken ct)
    {
        try
        {
            await _storageClient.DeleteAsync(_settings.StagingBucket, stagingPath, ct).ConfigureAwait(false);
        }
        catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted by a prior successful attempt.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete staging parquet {StagingPath}.", stagingPath);
        }
    }

    private async Task TryEnqueueCompactionAsync(StagingPathInfo pathInfo, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.DirectCompactionUrl))
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    _settings.DirectCompactionUrl,
                    new CompactionRequest(pathInfo.SourceHash, pathInfo.Model),
                    ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to invoke direct compaction for source {SourceHash} and model {Model}.",
                    pathInfo.SourceHash,
                    pathInfo.Model);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.CompactionQueue) || string.IsNullOrWhiteSpace(_settings.CompactionEndpointUrl))
            return;

        try
        {
            var task = new CloudTask
            {
                HttpRequest = new CloudTaskHttpRequest
                {
                    HttpMethod = CloudTaskHttpMethod.Post,
                    Url = _settings.CompactionEndpointUrl,
                    Headers = { ["Content-Type"] = "application/json" },
                    Body = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                        new CompactionRequest(pathInfo.SourceHash, pathInfo.Model)))
                }
            };

            ArgumentNullException.ThrowIfNull(_tasksClient);
            await _tasksClient.CreateTaskAsync(
                _settings.CompactionQueue,
                task,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to enqueue compaction for source {SourceHash} and model {Model}.",
                pathInfo.SourceHash,
                pathInfo.Model);
        }
    }

    private long GetNextPartTimestampMilliseconds()
    {
        while (true)
        {
            var lastIssued = Volatile.Read(ref _lastIssuedPartTimestamp);
            var candidate = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastIssued + 1);
            if (Interlocked.CompareExchange(ref _lastIssuedPartTimestamp, candidate, lastIssued) == lastIssued)
                return candidate;
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

    private static bool TryReadPartitionValue(string segment, string prefix, out string value)
    {
        value = string.Empty;

        if (!segment.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        value = segment[prefix.Length..];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
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

    private sealed record SourceMetadata(string Origin);

    private sealed record CompactionRequest(string Source, string Model);
}

/// <summary>
/// Purpose: Represents the shard identity encoded in a staging parquet path.
/// Complexity: Normalized path plus extracted source hash and model only.
/// </summary>
internal readonly record struct StagingPathInfo(string Path, string SourceHash, string Model)
{
    public string GetShardPrefix() => $"source={SourceHash}/model={Model}/";
}
