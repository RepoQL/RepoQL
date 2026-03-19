using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Cloud.Auth;
using RepoQL.Cloud.Service.Analytics;
using RepoQL.Cloud.Service.Embedding;
using RepoQL.Cloud.Service.Embedding.Cache;
using RepoQL.Cloud.Service.Inference;
using RepoQL.Embedding.Storage;

var builder = WebApplication.CreateBuilder(args);

// --- Observability ---

builder.Logging.AddOpenTelemetry();
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("RepoQL.Embedding.*")
        .AddMeter("RepoQL.Inference.*")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(t => t
        .AddSource("RepoQL.Embedding.*")
        .AddSource("RepoQL.Inference.*")
        .AddAspNetCoreInstrumentation())
    .UseOtlpExporter();

// --- gRPC ---

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 8 * 1024 * 1024; // 8 MB
    options.MaxSendMessageSize = 8 * 1024 * 1024;
    options.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Optimal;
    options.ResponseCompressionAlgorithm = "gzip";
    options.Interceptors.Add<AuthInterceptor>();
});

// --- Auth (shared) ---

builder.Services.AddRepoQlServerAuth(builder.Configuration.GetSection("Auth"));

// --- Embedding domain ---

builder.Services.Configure<EmbeddingServiceOptions>(builder.Configuration.GetSection("Embedding"));
builder.Services.AddHttpClient();

// Keyed VoyageAiClient instances: realtime (lite, fast) and batch (large, quality).
// Both share config but use different models. Embeddings are compatible (same v4 space).
builder.Services.AddKeyedSingleton<VoyageAiClient>("realtime");
builder.Services.AddKeyedSingleton<VoyageAiClient>("batch", (sp, _) =>
{
    var baseOptions = sp.GetRequiredService<IOptions<EmbeddingServiceOptions>>().Value;
    if (string.IsNullOrWhiteSpace(baseOptions.BatchModel) ||
        string.Equals(baseOptions.BatchModel, baseOptions.Model, StringComparison.OrdinalIgnoreCase))
    {
        // No separate batch model — reuse realtime client
        return sp.GetRequiredKeyedService<VoyageAiClient>("realtime");
    }

    var batchOptions = new EmbeddingServiceOptions
    {
        VoyageApiKey = baseOptions.VoyageApiKey,
        Model = baseOptions.BatchModel,
        Dimension = baseOptions.Dimension,
        OutputDtype = baseOptions.OutputDtype,
        VoyageBaseUrl = baseOptions.VoyageBaseUrl,
        TimeoutSeconds = baseOptions.TimeoutSeconds,
        Concurrency = baseOptions.Concurrency,
        RerankModel = baseOptions.RerankModel,
    };
    return new VoyageAiClient(Options.Create(batchOptions),
        sp.GetRequiredService<ILogger<VoyageAiClient>>());
});
// Default (unkeyed) resolves to realtime for backward compat
builder.Services.AddSingleton(sp => sp.GetRequiredKeyedService<VoyageAiClient>("realtime"));
builder.Services.Configure<CacheLayerSettings>(builder.Configuration.GetSection("CacheLayer"));
builder.Services.AddSingleton<IObjectStorageClient>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<CacheLayerSettings>>().Value;
    return ObjectStorageClientFactory.Create(settings.ToObjectStorageBackendSettings());
});
builder.Services.AddSingleton(typeof(EmbeddingCacheLayer), sp =>
{
    var settings = sp.GetRequiredService<IOptions<CacheLayerSettings>>().Value;
    var logger = sp.GetRequiredService<ILogger<EmbeddingCacheLayer>>();

    if (!EmbeddingCacheLayer.HasRequiredConfiguration(settings))
    {
        if (settings.Enabled)
        {
            logger.LogWarning(
                "Embedding cache layer enabled but configuration is incomplete. Falling back to direct Voyage relay.");
        }

        return null!;
    }

    try
    {
        return ActivatorUtilities.CreateInstance<EmbeddingCacheLayer>(sp);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize embedding cache layer. Falling back to direct Voyage relay.");
        return null!;
    }
});

// --- Inference domain ---

builder.Services.Configure<InferenceServiceOptions>(builder.Configuration.GetSection("Inference"));
builder.Services.AddSingleton<IXaiChatClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<InferenceServiceOptions>>().Value;
    var handler = new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true,
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
    };
    var channel = Grpc.Net.Client.GrpcChannel.ForAddress(options.Endpoint, new Grpc.Net.Client.GrpcChannelOptions
    {
        HttpHandler = handler,
        MaxReceiveMessageSize = 8 * 1024 * 1024,
        MaxSendMessageSize = 8 * 1024 * 1024,
    });
    var client = new XaiApi.Chat.ChatClient(channel);
    return new XaiChatClientAdapter(client);
});
builder.Services.AddSingleton<IGrokClient, GrokClient>();

// --- Analytics ---

builder.Services.AddSingleton<ProductAnalyticsStore>();

// --- Build ---

var app = builder.Build();

app.MapGrpcService<EmbeddingServiceImpl>();
app.MapGrpcService<InferenceServiceImpl>();

app.Run();
