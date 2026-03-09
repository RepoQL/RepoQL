using Google;
using Google.Cloud.Storage.V1;

namespace RepoQL.Embedding.Storage;

/// <summary>
/// Purpose: Adapts Google Cloud Storage to the embedding cache object storage contract.
/// Complexity: Straight-through GCS operations and status normalization only.
/// </summary>
public sealed class GcsObjectStorageClient : IObjectStorageClient
{
    private readonly StorageClient _storageClient;

    public GcsObjectStorageClient()
        : this(StorageClient.Create())
    {
    }

    public GcsObjectStorageClient(StorageClient storageClient)
    {
        _storageClient = storageClient;
    }

    public Task EnsureBucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task UploadAsync(string bucket, string path, Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageClient.UploadObjectAsync(
                bucket,
                path,
                "application/octet-stream",
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async Task DownloadAsync(string bucket, string path, Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageClient.DownloadObjectAsync(
                bucket,
                path,
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async IAsyncEnumerable<ObjectStorageObjectInfo> ListObjectsAsync(
        string bucket,
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerable<Google.Apis.Storage.v1.Data.Object> enumerable;

        try
        {
            enumerable = _storageClient.ListObjectsAsync(bucket, prefix);
        }
        catch (GoogleApiException ex)
        {
            throw ToObjectStorageException(ex, bucket, prefix);
        }

        await foreach (var item in enumerable.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                continue;

            yield return new ObjectStorageObjectInfo(item.Name, (item.Generation ?? 0L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public async Task DeleteAsync(string bucket, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageClient.DeleteObjectAsync(
                bucket,
                path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async Task UploadWithPreconditionAsync(
        string bucket,
        string path,
        Stream stream,
        string ifGenerationMatch,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(ifGenerationMatch, out var generationMatch))
            throw new InvalidOperationException($"GCS generation precondition '{ifGenerationMatch}' was not a valid integer.");

        try
        {
            await _storageClient.UploadObjectAsync(
                bucket,
                path,
                "application/octet-stream",
                stream,
                new UploadObjectOptions { IfGenerationMatch = generationMatch },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async Task<ObjectStorageObjectMetadata> GetObjectMetadataAsync(
        string bucket,
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var storageObject = await _storageClient.GetObjectAsync(
                bucket,
                path,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new ObjectStorageObjectMetadata((storageObject.Generation ?? 0L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (GoogleApiException ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    private static ObjectStorageException ToObjectStorageException(GoogleApiException ex, string bucket, string path)
        => new(ex.HttpStatusCode, $"GCS request failed for '{bucket}/{path}'.", ex);
}
