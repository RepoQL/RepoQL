using System.Collections.Concurrent;
using System.Diagnostics;
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
using Microsoft.Extensions.Configuration;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class HostCommands(IAnsiConsole console)
{
    public async Task Serve(string? repository = null, bool implicitStart = false)
    {
        var cwd = Directory.GetCurrentDirectory();
        repository ??= cwd;
        var repo = ProgramHelpers.ResolveRepo(repository);

        if (!implicitStart)
        {
            await TryShutdownExistingHostAsync(repo, CancellationToken.None).ConfigureAwait(false);
            await WaitForRepositoryAvailabilityAsync(repo, TimeSpan.FromSeconds(45), CancellationToken.None).ConfigureAwait(false);
        }
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Grpc"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:System"] = "Warning"
        });
        builder.Configuration.AddEnvironmentVariables();
        builder.Host.UseConsoleLifetime();
        // Reduce graceful shutdown timeout from default 30s to 5s
        // The indexing queues will be cancelled immediately on shutdown
        builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Logging.ClearProviders();
        if (!implicitStart)
        {
            builder.Logging.AddSimpleConsole(sc => sc.SingleLine = true);

        }

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
        // Restore persisted mounts BEFORE other hosted services start
        builder.Services.AddHostedService<MountRestorationService>();
        builder.Services.AddHostedService<IdleShutdownHostedService>();
        builder.Services.AddSingleton<InitialIndexingBarrier>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InitialIndexingBarrier>());
        builder.Services.AddSingleton<IInitialIndexingBarrier>(sp => sp.GetRequiredService<InitialIndexingBarrier>());
        builder.Services.AddSingleton<IQueryBarrier, QueryBarrier>();
        builder.Services.AddHostedService<PipelineHealthPublisher>();

        var app = builder.Build();
        
        // Always log startup info
        app.Logger.LogInformation("[Host] cwd={WorkingDirectory} repository={Repository} resolved repo={ResolvedRepository}", cwd, repository, repo);
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
                var exited = await WaitForProcessExitAsync(response.ProcessId, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                if (!exited)
                {
                    console.MarkupLine($"[red]Process {response.ProcessId} did not exit gracefully; force killing...[/]");
                    ForceKillProcess(response.ProcessId, console);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false); // Give OS time to release resources
                }
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
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessRunning(pid))
            {
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void ForceKillProcess(int pid, IAnsiConsole console)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Process already exited - that's fine
        }
        catch (InvalidOperationException ex)
        {
            console.MarkupLine($"[red]Failed to kill process {pid}: {Markup.Escape(ex.Message)}[/]");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            console.MarkupLine($"[red]Failed to kill process {pid}: {Markup.Escape(ex.Message)}[/]");
        }
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

    private static async Task WaitForRepositoryAvailabilityAsync(string repo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repo);
        var dbPath = Path.Combine(repoqlDir, "index.duckdb");
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDatabaseUnlocked(dbPath))
            {
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"RepoQL index at {dbPath} is still locked by another process after {timeout.TotalSeconds:F0}s.");
    }

    private static bool IsDatabaseUnlocked(string dbPath)
    {
        try
        {
            // If database doesn't exist yet, it's not locked
            if (!File.Exists(dbPath))
                return true;

            using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        const int ErrorSharingViolation = unchecked((int)0x80070020);
        const int ErrorLockViolation = unchecked((int)0x80070021);
        return ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation;
    }

}
