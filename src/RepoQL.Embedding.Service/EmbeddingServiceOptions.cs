namespace RepoQL.Embedding.Service;

/// <summary>
/// Configuration for the embedding service.
/// Bound to the "Embedding" section of appsettings.json.
/// </summary>
internal sealed class EmbeddingServiceOptions
{
    /// <summary>Voyage AI API key. Required.</summary>
    public string VoyageApiKey { get; set; } = "";

    /// <summary>Voyage AI model name.</summary>
    public string Model { get; set; } = "voyage-context-3";

    /// <summary>Output dimension for embeddings.</summary>
    public int Dimension { get; set; } = 1024;

    /// <summary>Voyage API base URL.</summary>
    public string VoyageBaseUrl { get; set; } = "https://api.voyageai.com/v1";

    /// <summary>Request timeout in seconds for Voyage API calls.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Max concurrent Voyage API calls for batch splitting.</summary>
    public int Concurrency { get; set; } = 4;
}

/// <summary>
/// Authentication configuration.
/// Bound to the "Auth" section of appsettings.json.
/// </summary>
internal sealed class AuthOptions
{
    /// <summary>
    /// SHA-256 hashes of valid API keys, hex-encoded.
    /// Compare: SHA256(incoming bearer token) against these values.
    /// </summary>
    public string[] ApiKeyHashes { get; set; } = [];
}
