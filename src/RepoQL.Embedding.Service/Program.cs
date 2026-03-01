using RepoQL.Embedding.Service;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.Configure<EmbeddingServiceOptions>(builder.Configuration.GetSection("Embedding"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

var app = builder.Build();

app.MapGrpcService<EmbeddingServiceImpl>();

app.Run();
