using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using RepoQL.Core;
using RepoQL.Contracts;
using RepoQL.Host.Options;
using Grpc.HealthCheck;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Documentation;
using Microsoft.Extensions.Hosting;
using RepoQL.Host.Services;

// Parse command line arguments
var repositoryPath = RepoLocator.FindRepoRoot(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());

// Determine implicit start (for auto-shutdown when idle)
var implicitStart = args.Contains("--implicit", StringComparer.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("REPOQL_IMPLICIT"), "1", StringComparison.Ordinal);

// Create and configure host
var builder = WebApplication.CreateBuilder(args);

// Configure logging - disable console logging when using ConsoleReporter
builder.Logging.ClearProviders();

builder.Logging.AddOpenTelemetry();
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("RepoQL.Indexing")
        .AddMeter("RepoQL.Host")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
    )   
    .WithTracing(t => t
        // Export spans created by the indexing pipeline
        .AddSource("RepoQL.Indexing")
        .AddSource("RepoQL.Host")
        .AddSource("RepoQL.Data.DuckDB")
        .AddAspNetCoreInstrumentation()
    )
    .UseOtlpExporter();

// Add gRPC
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 10 * 1024 * 1024; // 10MB
    options.MaxSendMessageSize = 10 * 1024 * 1024;
});

// gRPC Health checks
builder.Services.AddSingleton<HealthServiceImpl>();

// Store repository path for services that need it
builder.Services.AddSingleton(new RepositoryConfiguration { Path = repositoryPath });
builder.Services.AddSingleton(new HostState { RepositoryPath = repositoryPath, ImplicitStart = implicitStart, StartedAtUtc = DateTime.UtcNow });

// Configure Kestrel with Unix socket
builder.WebHost.ConfigureKestrel(options =>
{
    GrpcServerHelper.ConfigureUnixSocket(options, repositoryPath);
});

builder.Services.AddRepoIndexer(repositoryPath);
// Also index embedded documentation shipped with RepoQL.Data
builder.Services.AddEmbedStore(typeof(DocumentationMarker).Assembly);

// gRPC
builder.Services.AddGrpc();
builder.Services.AddHostedService<IdleShutdownHostedService>();
builder.Services.AddSingleton<InitialIndexingBarrier>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InitialIndexingBarrier>());
builder.Services.AddSingleton<IInitialIndexingBarrier>(sp => sp.GetRequiredService<InitialIndexingBarrier>());

var app = builder.Build();


// Map gRPC services
app.MapGrpcService<RepoQlServiceImpl>();
app.MapGrpcService<HealthServiceImpl>();

// Mark health as serving once app is built
var health = app.Services.GetRequiredService<HealthServiceImpl>();
health.SetStatus(string.Empty, Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving);
health.SetStatus("repoql.v1.RepoQL", Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving);

await app.RunAsync();

// Configuration class for repository settings
