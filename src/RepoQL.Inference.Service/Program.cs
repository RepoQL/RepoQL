using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Inference.Service;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddOpenTelemetry();
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("RepoQL.Inference.*")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(t => t
        .AddSource("RepoQL.Inference.*")
        .AddAspNetCoreInstrumentation())
    .UseOtlpExporter();

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 8 * 1024 * 1024;
    options.MaxSendMessageSize = 8 * 1024 * 1024;
    options.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Optimal;
    options.ResponseCompressionAlgorithm = "gzip";
    options.Interceptors.Add<ApiKeyAuthInterceptor>();
});

builder.Services.Configure<InferenceServiceOptions>(builder.Configuration.GetSection("Inference"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<ApiKeyAuthInterceptor>();
builder.Services.AddSingleton<IXaiChatClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<InferenceServiceOptions>>().Value;
    var channel = Grpc.Net.Client.GrpcChannel.ForAddress(options.Endpoint);
    var client = new XaiApi.Chat.ChatClient(channel);
    return new XaiChatClientAdapter(client);
});
builder.Services.AddSingleton<IGrokClient, GrokClient>();

var app = builder.Build();

app.MapGrpcService<InferenceServiceImpl>();

app.Run();
