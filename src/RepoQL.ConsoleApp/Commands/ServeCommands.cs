using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.Contracts;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Host;
using RepoQL.Core;
using RepoQL.Protocol;
using RepoQL.Protocol.Transport;
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
        if (!implicitStart)
        {
            await TryShutdownExistingHostAsync(repo, CancellationToken.None).ConfigureAwait(false);
        }
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
    /// <param name="cancel"> Used to cancel the reindex</param>
    public async Task Reindex(bool clear = false, CancellationToken cancel = default)
    {
        await using var client = await RepoQlClient.CreateAsync(cancellationToken: cancel);

                await console.Progress()
                    .AutoClear(false)
                    .AutoRefresh(true)
                    .Columns(new TaskDescriptionColumn(), new SpinnerColumn(), new PercentageColumn(), new ElapsedTimeColumn())
                    .StartAsync(async ctx =>
                    {
                        var tasks = new ConcurrentDictionary<string, ProgressTask>();
                        await foreach (var p in client.ReindexAllAsync(clear, timeout: TimeSpan.FromMinutes(15), cancellationToken: cancel))
                        {
                            var phase = p.Phase.ToString();
                            var task = tasks.GetOrAdd(phase, value => ctx.AddTask(value));
                            task.MaxValue = p.TotalItems > 0 ? p.TotalItems : 1;
                            task.Value = Math.Min(p.ProcessedItems, task.MaxValue);
                            task.Description = $"{phase} {p.ProcessedItems}";
                            if (task.Value >= task.MaxValue)
                                task.StopTask(); 
                        }

                        foreach (var task in tasks.Values) task.StopTask();
                    });

        console.MarkupLine("[green]Repopulation complete[/]");
    }

    private async Task TryShutdownExistingHostAsync(string repo, CancellationToken cancellationToken)
    {
        var socketPath = ProgramHelpers.ResolveSocketPath(repo);
        if (!File.Exists(socketPath))
            return;

        if (UnixSocketTransport.TryCleanupStaleSocket(socketPath))
            return;

        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };

            using var channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
            {
                HttpHandler = handler,
                Credentials = ChannelCredentials.Insecure
            });

            var client = new RepoQL.Contracts.RepoQL.RepoQLClient(channel);
            var response = await client.ShutdownHostAsync(new ShutdownHostRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.ProcessId > 0)
            {
                console.MarkupLine($"[yellow]Detected existing RepoQL host (PID {response.ProcessId}); requesting shutdown...[/]");
                await WaitForProcessExitAsync(response.ProcessId, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unavailable)
        {
            // host not reachable; stale socket will be cleaned up later
        }
        catch (SocketException)
        {
            // stale socket
        }
        catch (IOException)
        {
            // stale mapping file
        }
        catch (TimeoutException ex)
        {
            console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            throw;
        }
    }

    private static async Task WaitForProcessExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessRunning(pid))
            {
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Process {pid} did not exit within {timeout.TotalSeconds:F0}s.");
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

}
