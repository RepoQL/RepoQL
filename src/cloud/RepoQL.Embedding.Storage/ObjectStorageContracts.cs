using System.Net;

namespace RepoQL.Embedding.Storage;

/// <summary>
/// Purpose: Describes the object storage operations needed by the embedding cache services.
/// Complexity: CRUD, listing, and optimistic concurrency metadata only.
/// </summary>
public interface IObjectStorageClient
{
    Task EnsureBucketExistsAsync(string bucket, CancellationToken cancellationToken = default);

    Task UploadAsync(string bucket, string path, Stream stream, CancellationToken cancellationToken = default);

    Task DownloadAsync(string bucket, string path, Stream stream, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ObjectStorageObjectInfo> ListObjectsAsync(string bucket, string prefix, CancellationToken cancellationToken = default);

    Task DeleteAsync(string bucket, string path, CancellationToken cancellationToken = default);

    Task UploadWithPreconditionAsync(
        string bucket,
        string path,
        Stream stream,
        string ifGenerationMatch,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageObjectMetadata> GetObjectMetadataAsync(
        string bucket,
        string path,
        CancellationToken cancellationToken = default);
}

public sealed class ObjectStorageBackendSettings
{
    public string StorageBackend { get; set; } = "gcs";
    public string S3Endpoint { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";

    public bool IsGcs()
        => string.Equals(StorageBackend, StorageBackendKinds.Gcs, StringComparison.OrdinalIgnoreCase);

    public bool IsS3()
        => string.Equals(StorageBackend, StorageBackendKinds.S3, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (IsGcs())
            return;

        if (IsS3())
        {
            if (string.IsNullOrWhiteSpace(S3Endpoint))
                throw new InvalidOperationException("S3 storage backend requires a non-empty S3Endpoint.");

            if (string.IsNullOrWhiteSpace(S3AccessKey))
                throw new InvalidOperationException("S3 storage backend requires a non-empty S3AccessKey.");

            if (string.IsNullOrWhiteSpace(S3SecretKey))
                throw new InvalidOperationException("S3 storage backend requires a non-empty S3SecretKey.");

            return;
        }

        throw new InvalidOperationException($"Unsupported storage backend '{StorageBackend}'. Expected 'gcs' or 's3'.");
    }
}

public sealed record ObjectStorageObjectInfo(string Name, string Generation);

public sealed record ObjectStorageObjectMetadata(string Generation);

public static class ObjectStoragePreconditions
{
    public const string DoesNotExist = "0";
}

public static class StorageBackendKinds
{
    public const string Gcs = "gcs";
    public const string S3 = "s3";
}

public sealed class ObjectStorageException : Exception
{
    public ObjectStorageException()
    {
    }

    public ObjectStorageException(string message)
        : base(message)
    {
    }

    public ObjectStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ObjectStorageException(HttpStatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
