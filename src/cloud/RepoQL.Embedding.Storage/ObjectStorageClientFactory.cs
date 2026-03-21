namespace RepoQL.Embedding.Storage;

/// <summary>
/// Purpose: Creates the configured object storage client implementation.
/// Complexity: Backend selection and settings validation only.
/// </summary>
public static class ObjectStorageClientFactory
{
    public static IObjectStorageClient Create(ObjectStorageBackendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (settings.IsS3())
            return new S3ObjectStorageClient(settings);

        return new GcsObjectStorageClient();
    }
}
