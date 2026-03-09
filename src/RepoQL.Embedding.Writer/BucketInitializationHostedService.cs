using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoQL.Embedding.Storage;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Ensures local-development S3 buckets exist before the writer handles requests.
/// Complexity: Backend gate plus bucket creation only.
/// </summary>
internal sealed class BucketInitializationHostedService : IHostedService
{
    private readonly WriterSettings _settings;
    private readonly IObjectStorageClient _storageClient;
    private readonly ILogger<BucketInitializationHostedService> _logger;

    public BucketInitializationHostedService(
        IOptions<WriterSettings> settings,
        IObjectStorageClient storageClient,
        ILogger<BucketInitializationHostedService> logger)
    {
        _settings = settings.Value;
        _storageClient = storageClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.ToObjectStorageBackendSettings().IsS3())
            return;

        _logger.LogInformation("Ensuring MinIO buckets {EmbeddingsBucket} and {StagingBucket} exist.", _settings.EmbeddingsBucket, _settings.StagingBucket);
        await _storageClient.EnsureBucketExistsAsync(_settings.EmbeddingsBucket, cancellationToken).ConfigureAwait(false);
        await _storageClient.EnsureBucketExistsAsync(_settings.StagingBucket, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
