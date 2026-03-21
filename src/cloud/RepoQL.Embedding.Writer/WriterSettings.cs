using RepoQL.Embedding.Storage;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Holds Cloud Run writer configuration.
/// Complexity: Bucket names, task routing, and compaction tuning knobs only.
/// </summary>
public sealed class WriterSettings
{
    public string StorageBackend { get; set; } = "gcs";
    public string S3Endpoint { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string EmbeddingsBucket { get; set; } = "";
    public string StagingBucket { get; set; } = "";
    public string CompactionQueue { get; set; } = "";
    public string CompactionEndpointUrl { get; set; } = "";
    public string DirectCompactionUrl { get; set; } = "";
    public int PartCountThreshold { get; set; } = 20;
    public TimeSpan CompactionTtl { get; set; } = TimeSpan.FromDays(180);
    public int CompactionRowGroupSize { get; set; } = 50000;
    public TimeSpan CompactionStaleLockTimeout { get; set; } = TimeSpan.FromHours(1);

    public int CompactionThreshold
    {
        get => PartCountThreshold;
        set => PartCountThreshold = value;
    }

    public ObjectStorageBackendSettings ToObjectStorageBackendSettings()
        => new()
        {
            StorageBackend = StorageBackend,
            S3Endpoint = S3Endpoint,
            S3AccessKey = S3AccessKey,
            S3SecretKey = S3SecretKey
        };
}
