using RepoQL.Embedding.Writer;
using RepoQL.Embedding.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<WriterSettings>(builder.Configuration.GetSection("Writer"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IObjectStorageClient>(sp =>
{
    var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WriterSettings>>().Value;
    return ObjectStorageClientFactory.Create(settings.ToObjectStorageBackendSettings());
});
builder.Services.AddSingleton<CacheMergeHandler>();
builder.Services.AddSingleton<CompactionJob>();
builder.Services.AddHostedService<BucketInitializationHostedService>();

var app = builder.Build();

app.MapPost("/merge", MergeEndpoint.HandleAsync);
app.MapPost("/compact", CompactionEndpoint.HandleAsync);

app.Run();

public partial class Program;
