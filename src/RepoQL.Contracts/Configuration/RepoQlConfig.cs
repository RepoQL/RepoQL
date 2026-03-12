namespace RepoQL.Contracts.Configuration;

/// <summary>
/// Purpose: Single source of truth for every configurable setting in RepoQL.
/// Complexity: Nested classes group settings by concern. All properties nullable — null means
/// "use consumer's default." The class structure defines the key hierarchy.
/// </summary>
#pragma warning disable CA1034 // do not nest public classes
public sealed class RepoQlConfig
{
    public DuckDbSettings DuckDb { get; set; } = new();
    public EmbeddingSettings Embedding { get; set; } = new();
    public OrtSettings Ort { get; set; } = new();
    public InferenceSettings Inference { get; set; } = new();
    public CloudSettings Cloud { get; set; } = new();
    public HostSettings Host { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public DotnetSettings Dotnet { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public FindSettings Find { get; set; } = new();

    public sealed class DuckDbSettings
    {
        [Setting("DuckDB memory cap (e.g. 4GB, 512MB)",
            RequiresRestart = true, ValidValues = "e.g. 4GB, 512MB",
            LegacyEnvVar = "DUCKDB_MEMORY_LIMIT")]
        public string? MemoryLimit { get; set; }

        [Setting("DuckDB thread count",
            RequiresRestart = true,
            LegacyEnvVar = "DUCKDB_THREADS")]
        public int? Threads { get; set; }

        [Setting("DuckDB temp file location",
            RequiresRestart = true,
            LegacyEnvVar = "DUCKDB_TEMP_DIRECTORY")]
        public string? TempDirectory { get; set; }

        [Setting("Read connection pool size (1-4)",
            RequiresRestart = true, DefaultValue = "2",
            LegacyEnvVar = "DUCKDB_READ_POOL_SIZE")]
        public int? ReadPoolSize { get; set; }
    }

    public sealed class EmbeddingSettings
    {
        [Setting("Embedding generation mode",
            RequiresRestart = true, ValidValues = "none|structure|full|hybrid",
            DefaultValue = "hybrid",
            LegacyEnvVar = "REPOQL_EMBED_MODE")]
        public string? Mode { get; set; }

        [Setting("Path to ONNX model override",
            RequiresRestart = true,
            LegacyEnvVar = "REPOQL_EMBED_MODEL_PATH")]
        public string? ModelPath { get; set; }

        [Setting("Max tokens per embedding sample",
            RequiresRestart = true, DefaultValue = "256",
            LegacyEnvVar = "REPOQL_EMBED_MAX_TOKENS")]
        public int? MaxTokens { get; set; }

        [Setting("Embedding dimension for hashed provider",
            RequiresRestart = true, DefaultValue = "384",
            LegacyEnvVar = "REPOQL_EMBED_DIM")]
        public int? Dim { get; set; }

        [Setting("Batch size for embedding generation",
            LegacyEnvVar = "REPOQL_EMBED_BATCH_SIZE")]
        public int? BatchSize { get; set; }

        [Setting("Concurrency for vector indexing",
            LegacyEnvVar = "REPOQL_EMBED_CONCURRENCY")]
        public int? Concurrency { get; set; }

        public RemoteEmbeddingSettings Remote { get; set; } = new();

        public EmbeddingCacheSettings Cache { get; set; } = new();
    }

    /// <summary>
    /// Purpose: Configure remote contextual embedding service connection.
    /// Complexity: URL + API key + timeout. When URL is set, remote provider is active.
    /// </summary>
    public sealed class RemoteEmbeddingSettings
    {
#if DEBUG
        [Setting("gRPC endpoint URL for the remote embedding service",
            RequiresRestart = true,
            DefaultValue = "https://api.repoql.ai")]
        public string? Url { get; set; } = "https://api.repoql.ai";
#else
        public string? Url { get; } = "https://api.repoql.ai";
#endif

        [Setting("Request timeout in seconds",
            RequiresRestart = true, DefaultValue = "30")]
        public int? TimeoutSeconds { get; set; }
    }

    /// <summary>
    /// Purpose: Configure local parquet-backed embedding cache behavior.
    /// Complexity: Holds cache enablement, storage location, and maintenance thresholds.
    /// </summary>
    public sealed class EmbeddingCacheSettings
    {
        [Setting("Enable embedding cache",
            DefaultValue = "true")]
        public bool? Enabled { get; set; }

        [Setting("Local embedding cache directory",
            DefaultValue = "~/.repoql/embedding-cache/")]
        public string? Path { get; set; }

        [Setting("Cache directory paths (first is write target, rest are read-only)",
            DefaultValue = "~/.repoql/embedding-cache/")]
        public List<string>? Paths { get; set; }

        [Setting("Compact embedding cache when file count exceeds this threshold",
            DefaultValue = "100")]
        public int? CompactionThreshold { get; set; }

        [Setting("Maximum embedding cache size in MB (0 = unlimited)",
            DefaultValue = "500")]
        public int? MaxSizeMb { get; set; }
    }

    public sealed class OrtSettings
    {
        [Setting("ONNX Runtime execution provider",
            RequiresRestart = true, ValidValues = "CPU|CUDA|DML|COREML",
            DefaultValue = "CPU",
            LegacyEnvVar = "REPOQL_ORT_PROVIDER")]
        public string? Provider { get; set; }

        [Setting("ONNX intra-op thread count (0 = auto)",
            RequiresRestart = true, DefaultValue = "0",
            LegacyEnvVar = "REPOQL_ORT_INTRA_THREADS")]
        public int? IntraThreads { get; set; }

        [Setting("ONNX inter-op thread count",
            RequiresRestart = true, DefaultValue = "1",
            LegacyEnvVar = "REPOQL_ORT_INTER_THREADS")]
        public int? InterThreads { get; set; }
    }

    /// <summary>
    /// Purpose: Shared authentication for all RepoQL cloud services (embedding, inference).
    /// Complexity: Supports legacy API keys plus OAuth access/refresh token configuration for
    /// client-side credential refresh.
    /// </summary>
    public sealed class CloudSettings
    {
        public const string DefaultWorkOsApiBase = "https://api.workos.com";
#if DEBUG
        public const string DefaultAuthKitBase = "https://peaceful-puddle-33-staging.authkit.app";
        public const string DefaultClientId = "client_01KKDYHCHTDAYE939PAKGFV60D";
#else
        public const string DefaultAuthKitBase = "https://seamless-surprise-18.authkit.app";
        public const string DefaultClientId = "client_01KKDYHD5DF3E3SHDPPN3BMB57";
#endif
        public const string DefaultAuthenticateEndpoint = DefaultWorkOsApiBase + "/user_management/authenticate";
        public const string DefaultAuthorizationEndpoint = DefaultWorkOsApiBase + "/user_management/authorize";
        public const string DefaultDeviceAuthorizationEndpoint = DefaultWorkOsApiBase + "/user_management/authorize/device";

        [Setting("Legacy API key for RepoQL cloud services (embedding, inference)",
            Sensitive = true, RequiresRestart = true)]
        public string? ApiKey { get; set; }

        [Setting("OAuth client ID for RepoQL cloud token refresh",
            RequiresRestart = true,
            DefaultValue = DefaultClientId)]
        public string? ClientId { get; set; } = DefaultClientId;

#if DEBUG
        [Setting("WorkOS authenticate endpoint for cloud login and refresh (debug only)",
            RequiresRestart = true,
            DefaultValue = DefaultAuthenticateEndpoint)]
        public string? AuthenticateEndpoint { get; set; } = DefaultAuthenticateEndpoint;

        [Setting("WorkOS authorization endpoint for cloud browser login (debug only)",
            RequiresRestart = true,
            DefaultValue = DefaultAuthorizationEndpoint)]
        public string? AuthorizationEndpoint { get; set; } = DefaultAuthorizationEndpoint;

        [Setting("WorkOS device authorization endpoint for cloud device login (debug only)",
            RequiresRestart = true,
            DefaultValue = DefaultDeviceAuthorizationEndpoint)]
        public string? DeviceAuthorizationEndpoint { get; set; } = DefaultDeviceAuthorizationEndpoint;
#endif

        [Setting("Current RepoQL cloud access token (JWT)",
            Sensitive = true, RequiresRestart = true)]
        public string? AuthToken { get; set; }

        [Setting("Current RepoQL cloud refresh token",
            Sensitive = true, RequiresRestart = true)]
        public string? RefreshToken { get; set; }
    }

    /// <summary>
    /// Purpose: Configure remote inference service connection and tool-loop defaults.
    /// Complexity: Holds endpoint/auth settings plus explain-specific tool budget limits.
    /// </summary>
    public sealed class InferenceSettings
    {
#if DEBUG
        [Setting("Inference service gRPC URL",
            RequiresRestart = true,
            DefaultValue = "https://api.repoql.ai")]
        public string? ServiceUrl { get; set; } = "https://api.repoql.ai";
#else
        public string? ServiceUrl { get; } = "https://api.repoql.ai";
#endif

        [Setting("Default tool token budget for explain",
            DefaultValue = "30000")]
        public int ToolTokenBudget { get; set; } = 30_000;

        [Setting("Default max tool rounds for explain",
            DefaultValue = "5")]
        public int MaxRounds { get; set; } = 5;
    }

    public sealed class HostSettings
    {
        [Setting("Seconds before idle host shuts down",
            DefaultValue = "45",
            LegacyEnvVar = "REPOQL_IDLE_GRACE_SECONDS")]
        public int? IdleGraceSeconds { get; set; }

        [Setting("Client lease TTL in seconds",
            DefaultValue = "30",
            LegacyEnvVar = "REPOQL_LEASE_TTL_SECONDS")]
        public int? LeaseTtlSeconds { get; set; }

        [Setting("Watchdog timeout after shutdown in seconds",
            DefaultValue = "15",
            LegacyEnvVar = "REPOQL_IMPLICIT_SHUTDOWN_WATCHDOG_SECONDS")]
        public int? ShutdownWatchdogSeconds { get; set; }

        [Setting("Host startup timeout in milliseconds",
            DefaultValue = "120000",
            LegacyEnvVar = "REPOQL_START_TIMEOUT_MS")]
        public int? StartTimeoutMs { get; set; }

        [Setting("Lease establishment timeout in milliseconds",
            DefaultValue = "5000",
            LegacyEnvVar = "REPOQL_LEASE_START_TIMEOUT_MS")]
        public int? LeaseStartTimeoutMs { get; set; }

        [Setting("RPC hang detection threshold in milliseconds",
            DefaultValue = "30000",
            LegacyEnvVar = "REPOQL_RPC_HANG_THRESHOLD_MS")]
        public int? RpcHangThresholdMs { get; set; }

        [Setting("Maximum concurrent gRPC queries (query/explore/read)",
            DefaultValue = "4")]
        public int? MaxConcurrentQueries { get; set; }
    }

    public sealed class McpSettings
    {
        [Setting("Load global agent MCP configs",
            DefaultValue = "true",
            LegacyEnvVar = "REPOQL_MCP_INCLUDE_GLOBALS")]
        public bool? IncludeGlobals { get; set; }

        [Setting("Comma-separated list of enabled agent types",
            LegacyEnvVar = "REPOQL_MCP_ENABLED_AGENTS")]
        public string? EnabledAgents { get; set; }
    }

    public sealed class DotnetSettings
    {
        [Setting("Enable deep Roslyn analysis (expensive)",
            DefaultValue = "false",
            LegacyEnvVar = "REPOQL_DOTNET_ANALYSIS")]
        public bool? Analysis { get; set; }

        [Setting("Roslyn workspace session sliding expiration (seconds)",
            DefaultValue = "60",
            LegacyEnvVar = "REPOQL_CSHARP_WORKSPACE_SESSION_SLIDING_SECONDS")]
        public int? CsharpWorkspaceSessionSlidingSeconds { get; set; }

        [Setting("Roslyn workspace session absolute expiration (seconds)",
            DefaultValue = "600",
            LegacyEnvVar = "REPOQL_CSHARP_WORKSPACE_SESSION_ABSOLUTE_SECONDS")]
        public int? CsharpWorkspaceSessionAbsoluteSeconds { get; set; }

        [Setting("Roslyn workspace session cache entry size",
            DefaultValue = "1",
            LegacyEnvVar = "REPOQL_CSHARP_WORKSPACE_SESSION_ENTRY_SIZE")]
        public int? CsharpWorkspaceSessionEntrySize { get; set; }
    }

    public sealed class CacheSettings
    {
        [Setting("Shared memory cache size limit",
            RequiresRestart = true, DefaultValue = "128",
            LegacyEnvVar = "REPOQL_SHARED_CACHE_SIZE_LIMIT")]
        public long? SizeLimit { get; set; }
    }

    public sealed class SearchSettings
    {
        [Setting("Enable document reranking",
            DefaultValue = "true")]
        public bool? RerankEnabled { get; set; }
    }

    public sealed class FindSettings
    {
        [Setting("Maximum files allowed in read => find scope before refusing broad search",
            DefaultValue = "64")]
        public int? MaxScopeDocuments { get; set; }

        [Setting("Max find results returned after scoring",
            DefaultValue = "20")]
        public int? MaxResults { get; set; }

        [Setting("Minimum score threshold for accepted find matches",
            DefaultValue = "0.10")]
        public double? MinScoreThreshold { get; set; }

        [Setting("Initial semantic candidate chunk limit per find round",
            DefaultValue = "96")]
        public int? InitialCandidateLimit { get; set; }

        [Setting("Maximum semantic candidate chunk limit across adaptive widening",
            DefaultValue = "768")]
        public int? MaxCandidateLimit { get; set; }

        [Setting("Adaptive widening growth percentage per round (200 = 2x)",
            DefaultValue = "200")]
        public int? GrowthPercent { get; set; }

        [Setting("Maximum adaptive widening rounds for find",
            DefaultValue = "4")]
        public int? MaxWideningRounds { get; set; }

        [Setting("Target number of qualified matches before stopping widening",
            DefaultValue = "24")]
        public int? TargetQualifiedMatches { get; set; }

        [Setting("Confidence margin required to stop widening early",
            DefaultValue = "0.05")]
        public double? ConfidenceMargin { get; set; }

        [Setting("Precomputed chunk cap per document during candidate selection",
            DefaultValue = "3")]
        public int? PerDocumentChunkLimit { get; set; }

        [Setting("Maximum new chunks to refine with zoom_and_enhance per round",
            DefaultValue = "192")]
        public int? MaxZoomInputsPerRound { get; set; }

        [Setting("Minimum line span for zoom_and_enhance splits",
            DefaultValue = "8")]
        public int? ZoomMinLines { get; set; }

        [Setting("Maximum split depth for zoom_and_enhance",
            DefaultValue = "3")]
        public int? ZoomMaxDepth { get; set; }

        [Setting("Score threshold used by zoom_and_enhance split acceptance",
            DefaultValue = "0.20")]
        public double? ZoomThreshold { get; set; }

        [Setting("Context lines around refined matches in find output",
            DefaultValue = "2")]
        public int? ContextLines { get; set; }

        [Setting("Per-round timeout in milliseconds for find SQL phases",
            DefaultValue = "20000")]
        public int? RoundTimeoutMs { get; set; }

        [Setting("Total timeout in milliseconds across all adaptive find rounds",
            DefaultValue = "90000")]
        public int? TotalTimeoutMs { get; set; }
    }
}
#pragma warning restore CA1034
