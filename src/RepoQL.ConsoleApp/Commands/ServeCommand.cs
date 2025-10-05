using RepoQL.App.Host;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Documentation;
using ConsoleAppFramework;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Core;

public class ServeCommand
{
    [Command("serve")]
    public async Task Run(string? repositoryRoot, CancellationToken cancellationToken)
    {
        var repo = ProgramHelpers.ResolveRepo(repositoryRoot);
        var builder = WebApplication.CreateBuilder([]);

        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m.AddMeter("RepoQL.Indexing").AddMeter("RepoQL.Host").AddAspNetCoreInstrumentation().AddRuntimeInstrumentation())
            .WithTracing(t => t.AddSource("RepoQL.Indexing").AddSource("RepoQL.Host").AddSource("RepoQL.Data.DuckDB").AddAspNetCoreInstrumentation())
            .UseOtlpExporter();

        builder.Services.AddGrpc(o => { o.MaxReceiveMessageSize = 10 * 1024 * 1024; o.MaxSendMessageSize = 10 * 1024 * 1024; });
        builder.Services.AddSingleton<HealthServiceImpl>();
        builder.Services.AddSingleton(new RepositoryConfiguration { Path = repo });
        builder.Services.AddSingleton(new HostState { RepositoryPath = repo, ImplicitStart = false, StartedAtUtc = DateTime.UtcNow });
        builder.WebHost.ConfigureKestrel(options => { GrpcServerHelper.ConfigureUnixSocket(options, repo); });
        builder.Services.AddRepoIndexer(repo);
        builder.Services.AddEmbedStore(typeof(DocumentationMarker).Assembly);
        builder.Services.AddGrpc();
        builder.Services.AddHostedService<IdleShutdownHostedService>();
        builder.Services.AddSingleton<InitialIndexingBarrier>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InitialIndexingBarrier>());
        builder.Services.AddSingleton<IInitialIndexingBarrier>(sp => sp.GetRequiredService<InitialIndexingBarrier>());

        var app = builder.Build();
        app.MapGrpcService<RepoQlServiceImpl>();
        app.MapGrpcService<HealthServiceImpl>();
        var health = app.Services.GetRequiredService<HealthServiceImpl>();
        health.SetStatus(string.Empty, HealthCheckResponse.Types.ServingStatus.Serving);
        health.SetStatus("repoql.v1.RepoQL", HealthCheckResponse.Types.ServingStatus.Serving);
        await app.RunAsync().ConfigureAwait(false);
    }
}