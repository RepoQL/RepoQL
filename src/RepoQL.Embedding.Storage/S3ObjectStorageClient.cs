using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace RepoQL.Embedding.Storage;

/// <summary>
/// Purpose: Adapts S3-compatible object storage to the embedding cache object storage contract.
/// Complexity: MinIO-ready client setup, CRUD, and conditional writes via ETag-based preconditions only.
/// </summary>
public sealed class S3ObjectStorageClient : IObjectStorageClient, IDisposable
{
    private readonly AmazonS3Client _client;

    public S3ObjectStorageClient(ObjectStorageBackendSettings settings)
    {
        var endpoint = new Uri(settings.S3Endpoint, UriKind.Absolute);
        var config = new AmazonS3Config
        {
            ServiceURL = settings.S3Endpoint,
            ForcePathStyle = true,
            UseHttp = string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase),
            AuthenticationRegion = "us-east-1"
        };

        _client = new AmazonS3Client(
            new BasicAWSCredentials(settings.S3AccessKey, settings.S3SecretKey),
            config);
    }

    public async Task UploadAsync(string bucket, string path, Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = path,
                InputStream = stream
            };

            await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async Task DownloadAsync(string bucket, string path, Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(bucket, path, cancellationToken).ConfigureAwait(false);
            await response.ResponseStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async IAsyncEnumerable<ObjectStorageObjectInfo> ListObjectsAsync(
        string bucket,
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;

        do
        {
            ListObjectsV2Response response;
            try
            {
                response = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = prefix,
                        ContinuationToken = continuationToken
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                throw ToObjectStorageException(ex, bucket, prefix);
            }

            foreach (var item in response.S3Objects)
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                    continue;

                yield return new ObjectStorageObjectInfo(item.Key, NormalizeEntityTag(item.ETag));
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    public async Task DeleteAsync(string bucket, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.DeleteObjectAsync(bucket, path, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
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
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = path,
                InputStream = stream
            };

            if (string.Equals(ifGenerationMatch, ObjectStoragePreconditions.DoesNotExist, StringComparison.Ordinal))
                request.IfNoneMatch = "*";
            else
                request.IfMatch = NormalizeEntityTag(ifGenerationMatch);

            await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ObjectStorageException(HttpStatusCode.PreconditionFailed, $"S3 precondition failed for '{bucket}/{path}'.", ex);
        }
        catch (AmazonS3Exception ex)
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
            var response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = path
                },
                cancellationToken).ConfigureAwait(false);

            return new ObjectStorageObjectMetadata(NormalizeEntityTag(response.ETag));
        }
        catch (AmazonS3Exception ex)
        {
            throw ToObjectStorageException(ex, bucket, path);
        }
    }

    public async Task EnsureBucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        try
        {
            await _client.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Bucket does not exist yet.
        }

        await _client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static string NormalizeEntityTag(string? eTag)
    {
        if (string.IsNullOrWhiteSpace(eTag))
            return string.Empty;

        return eTag.Trim();
    }

    private static ObjectStorageException ToObjectStorageException(AmazonS3Exception ex, string bucket, string path)
        => new(ex.StatusCode, $"S3 request failed for '{bucket}/{path}'.", ex);
}
