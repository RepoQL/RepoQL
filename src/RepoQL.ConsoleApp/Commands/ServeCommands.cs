using System.Collections.Concurrent;
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
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Host;
using RepoQL.Core;
using RepoQL.Protocol;
using Spectre.Console;
using ConsoleAppFramework;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class HostCommands(IAnsiConsole console)
{
    public async Task Serve(string? repository = null, bool implicitStart = false)
    {
        repository ??= Directory.GetCurrentDirectory();
        var repo = ProgramHelpers.ResolveRepo(repository);
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.Host.UseConsoleLifetime();
        builder.Logging.ClearProviders();
        if (!implicitStart)
        {
            builder.Logging.AddSimpleConsole(sc => sc.SingleLine = true);

        }

        builder.Logging.AddFilter((s, level) => !s.StartsWith("Microsoft.AspNetCore") && level >= LogLevel.Information);
        builder.Logging.AddOpenTelemetry();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddMeter("RepoQL.*")
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddSource("RepoQL.*")
                .AddAspNetCoreInstrumentation())
            .UseOtlpExporter();

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<HealthServiceImpl>();
        builder.Services.AddSingleton(new RepositoryConfiguration { Path = repo });
        builder.Services.AddSingleton(new HostState
        {
            RepositoryPath = repo, 
            ImplicitStart = implicitStart, 
            StartedAtUtc = DateTime.UtcNow
        });
        builder.WebHost.ConfigureKestrel(options => { GrpcServerHelper.ConfigureUnixSocket(options, repo); });
        builder.Services.AddRepoIndexer(repo);
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<HostMetrics>();
        builder.Services.AddHostedService<IdleShutdownHostedService>();
        builder.Services.AddSingleton<InitialIndexingBarrier>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InitialIndexingBarrier>());
        builder.Services.AddSingleton<IInitialIndexingBarrier>(sp => sp.GetRequiredService<InitialIndexingBarrier>());
        builder.Services.AddHostedService<PipelineHealthPublisher>();

        var app = builder.Build();
        app.MapGrpcService<RepoQlServiceImpl>();
        app.MapGrpcService<HealthServiceImpl>();
        var health = app.Services.GetRequiredService<HealthServiceImpl>();
        health.SetStatus(string.Empty, HealthCheckResponse.Types.ServingStatus.Serving);
        health.SetStatus("repoql.v1.RepoQL", HealthCheckResponse.Types.ServingStatus.Serving);
        await app.RunAsync().ConfigureAwait(false);
    }
    
    /// <summary>
    /// Re-populate (re-index) the entire repository database. Streams phase/progress via gRPC and shows a progress bar.
    /// </summary>
    /// <param name="clear">When true, requests the server to clear existing data first (best-effort).</param>
    public async Task Reindex(bool clear = false, CancellationToken cancel = default)
    {
        await using var client = await RepoQlClient.CreateAsync(cancellationToken: cancel);

                await console.Progress()
                    .AutoClear(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn())
                    .StartAsync(async ctx =>
                    {
                        var tasks = new ConcurrentDictionary<string, ProgressTask>();
                        await foreach (var p in client.ReindexAllAsync(clear, timeout: TimeSpan.FromMinutes(10), cancellationToken: cancel))
                        {
                            var phase = p.Phase.ToString();
                            var task = tasks.GetOrAdd(phase, value => ctx.AddTask(value));
                            task.MaxValue = p.TotalItems > 0 ? p.TotalItems : 1;
                            task.Value = Math.Min(p.ProcessedItems, task.MaxValue);
                        }

                        foreach (var task in tasks.Values) task.StopTask();
                    });

        console.MarkupLine("[green]Repopulation complete[/]");
    }

}
