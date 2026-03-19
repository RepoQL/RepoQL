using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics.CodeAnalysis;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Cloud.Auth;
using RepoQL.Cloud.Service.Analytics;
using RepoQL.Cloud.Service.Embedding;
using RepoQL.Cloud.Service.Embedding.Cache;
using RepoQL.Cloud.Service.Inference;
using RepoQL.Embedding.Storage;

var builder = WebApplication.CreateBuilder(args);
var otlpExporterConfiguration = CloudServiceOtlpExportConfiguration.TryCreate();

// --- Observability ---

builder.Logging.AddOpenTelemetry(logging =>
{
    otlpExporterConfiguration?.Apply(logging);
});

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("RepoQL.Embedding.*")
        .AddMeter("RepoQL.Inference.*")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .ApplyExporterConfiguration(otlpExporterConfiguration))
    .WithTracing(t => t
        .AddSource("RepoQL.Embedding.*")
        .AddSource("RepoQL.Inference.*")
        .AddAspNetCoreInstrumentation()
        .ApplyExporterConfiguration(otlpExporterConfiguration));

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

internal static class CloudServiceOtlpExportConfiguration
{
    private const string OtlpEndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string GoogleCloudRunServiceEnvironmentVariable = "K_SERVICE";
    private const string GoogleComputeMetadataEnvironmentVariable = "GCE_METADATA";
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private static readonly Uri GoogleCloudTelemetryEndpoint = new("https://telemetry.googleapis.com");

    public static OtlpExporterConfiguration? TryCreate()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OtlpEndpointEnvironmentVariable)))
        {
            return OtlpExporterConfiguration.UseDefaults;
        }

        if (!IsRunningOnGoogleCloud())
        {
            return null;
        }

        try
        {
            var credential = GoogleCredential.GetApplicationDefault();
            if (credential.IsCreateScopedRequired)
            {
                credential = credential.CreateScoped(CloudPlatformScope);
            }

            credential = credential.CreateWithEnvironmentQuotaProject();
            return new OtlpExporterConfiguration(options =>
            {
                options.Endpoint = GoogleCloudTelemetryEndpoint;
                options.Protocol = OtlpExportProtocol.Grpc;
                options.HttpClientFactory = () => CreateGoogleCloudTelemetryClient(credential);
            });
        }
        catch
        {
            // No ADC available on startup. Skip exporting instead of failing the service.
            return null;
        }
    }

    private static bool IsRunningOnGoogleCloud()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GoogleCloudRunServiceEnvironmentVariable)) ||
           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GoogleComputeMetadataEnvironmentVariable));

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient owns and disposes the handler pipeline.")]
    private static HttpClient CreateGoogleCloudTelemetryClient(GoogleCredential credential)
    {
        var transport = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var authHandler = new GoogleCloudTelemetryAuthHandler(credential)
        {
            InnerHandler = transport,
        };

        return new HttpClient(authHandler, disposeHandler: true)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }
}

internal sealed class OtlpExporterConfiguration
{
    public static readonly OtlpExporterConfiguration UseDefaults = new();

    private readonly Action<OtlpExporterOptions>? configure;

    public OtlpExporterConfiguration()
    {
    }

    public OtlpExporterConfiguration(Action<OtlpExporterOptions> configure)
    {
        this.configure = configure;
    }

    public void Apply(OpenTelemetryLoggerOptions logging)
    {
        if (configure is null)
        {
            logging.AddOtlpExporter();
            return;
        }

        logging.AddOtlpExporter(configure);
    }

    public void Apply(MeterProviderBuilder metrics)
    {
        if (configure is null)
        {
            metrics.AddOtlpExporter();
            return;
        }

        metrics.AddOtlpExporter(configure);
    }

    public void Apply(TracerProviderBuilder tracing)
    {
        if (configure is null)
        {
            tracing.AddOtlpExporter();
            return;
        }

        tracing.AddOtlpExporter(configure);
    }
}

internal sealed class GoogleCloudTelemetryAuthHandler(GoogleCredential credential) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        if (credential.UnderlyingCredential is ITokenAccessWithHeaders tokenAccessWithHeaders)
        {
            var tokenWithHeaders =
                await tokenAccessWithHeaders.GetAccessTokenWithHeadersForRequestAsync(
                    request.RequestUri?.ToString(),
                    cancellationToken);

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", tokenWithHeaders.AccessToken);
            tokenWithHeaders.AddHeaders(request);
        }
        else if (credential.UnderlyingCredential is ITokenAccess tokenAccess)
        {
            var token = await tokenAccess.GetAccessTokenForRequestAsync(
                request.RequestUri?.ToString(),
                cancellationToken);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            if (!string.IsNullOrWhiteSpace(credential.QuotaProject))
            {
                request.Headers.TryAddWithoutValidation("x-goog-user-project", credential.QuotaProject);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

internal static class OpenTelemetryExporterConfigurationExtensions
{
    public static MeterProviderBuilder ApplyExporterConfiguration(
        this MeterProviderBuilder builder,
        OtlpExporterConfiguration? exporterConfiguration)
    {
        exporterConfiguration?.Apply(builder);
        return builder;
    }

    public static TracerProviderBuilder ApplyExporterConfiguration(
        this TracerProviderBuilder builder,
        OtlpExporterConfiguration? exporterConfiguration)
    {
        exporterConfiguration?.Apply(builder);
        return builder;
    }
}
