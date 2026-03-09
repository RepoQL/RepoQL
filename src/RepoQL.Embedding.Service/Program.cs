using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Embedding.Service;
using RepoQL.Embedding.Service.Cache;
using RepoQL.Embedding.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddOpenTelemetry();
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("RepoQL.Embedding.*")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(t => t
        .AddSource("RepoQL.Embedding.*")
        .AddAspNetCoreInstrumentation())
    .UseOtlpExporter();

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 8 * 1024 * 1024; // 8 MB
    options.MaxSendMessageSize = 8 * 1024 * 1024;
    options.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Optimal;
    options.ResponseCompressionAlgorithm = "gzip";
    options.Interceptors.Add<ApiKeyAuthInterceptor>();
});

builder.Services.AddSingleton<VoyageAiClient>();
builder.Services.AddSingleton<ApiKeyAuthInterceptor>();
builder.Services.AddHttpClient();
builder.Services.Configure<EmbeddingServiceOptions>(builder.Configuration.GetSection("Embedding"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
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

var app = builder.Build();

app.MapGrpcService<EmbeddingServiceImpl>();

app.Run();
