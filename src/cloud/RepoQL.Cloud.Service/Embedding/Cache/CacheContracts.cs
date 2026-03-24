using RepoQL.Embedding.Storage;

namespace RepoQL.Cloud.Service.Embedding.Cache;

public record ChunkFingerprint(int OriginalIndex, string Sha256, string Text, string Context);

public record CacheLookupResult(
    IReadOnlyDictionary<string, byte[]> Hits,
    IReadOnlyList<ChunkFingerprint> Misses);

public record CacheEntry(string Sha256, byte[] Vector, DateTimeOffset CreatedAt);

public sealed class CacheLayerSettings
{
    public string StorageBackend { get; set; } = "gcs";
    public string S3Endpoint { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string EmbeddingsBucket { get; set; } = "";
    public string StagingBucket { get; set; } = "";
    public string DirectWriterUrl { get; set; } = "";

    public ObjectStorageBackendSettings ToObjectStorageBackendSettings()
        => new()
        {
            StorageBackend = StorageBackend,
            S3Endpoint = S3Endpoint,
            S3AccessKey = S3AccessKey,
            S3SecretKey = S3SecretKey
        };
}
