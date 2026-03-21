namespace RepoQL.Embedding.Service;

/// <summary>
/// Configuration for the embedding service.
/// Bound to the "Embedding" section of appsettings.json.
/// </summary>
internal sealed class EmbeddingServiceOptions
{
    /// <summary>Voyage AI API key. Required.</summary>
    public string VoyageApiKey { get; set; } = "";

    /// <summary>Voyage AI model name. Voyage 4 models use the standard endpoint; context-3 uses contextual.</summary>
    public string Model { get; set; } = "voyage-4-large";

    /// <summary>Output dimension for embeddings.</summary>
    public int Dimension { get; set; } = 1024;

    /// <summary>Output data type: "float", "int8", "uint8", "binary", "ubinary".</summary>
    public string OutputDtype { get; set; } = "int8";

    /// <summary>Voyage API base URL.</summary>
    public string VoyageBaseUrl { get; set; } = "https://api.voyageai.com/v1";

    /// <summary>Request timeout in seconds for Voyage API calls.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Max concurrent Voyage API calls for batch splitting.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>Voyage reranking model. Used when Rerank RPC model field is empty.</summary>
    public string RerankModel { get; set; } = "rerank-2.5";
}
