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
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Host;
using RepoQL.ConsoleApp.Logging;
using RepoQL.ConsoleApp.Search;
using RepoQL.Core;
using RepoQL.Indexing.Hosting;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;
using RepoQL.Protocol;
using RepoQL.Protocol.Transport;
using RepoQL.Explore;
using RepoQL.Explore.Search;
using Serilog;
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
        var (serilogLogger, _) = HostLogging.Initialize(repo);
        var version = HostLogging.GetHostVersion();
        serilogLogger.Information("Host starting (pid={Pid} version={Version})", Environment.ProcessId, version);
        serilogLogger.Information("Phase: preflight");

        // Always try to shutdown existing host to prevent multiple hosts writing to the same database
        // This prevents write-write conflicts from concurrent access
        await TryShutdownExistingHostAsync(repo, implicitStart, serilogLogger, CancellationToken.None).ConfigureAwait(false);
        var hostLock = await WaitForHostLockAsync(repo, TimeSpan.FromSeconds(45), implicitStart, serilogLogger, CancellationToken.None)
            .ConfigureAwait(false);
        if (hostLock is null)
        {
            if (!implicitStart)
            {
                var lockPath = HostLock.GetLockPath(repo);
                console.MarkupLine($"[yellow]Host already running; lock held at {Markup.Escape(lockPath)}[/]");
            }

            return;
        }

        try
        {
            await WaitForRepositoryAvailabilityAsync(repo, TimeSpan.FromSeconds(45), CancellationToken.None).ConfigureAwait(false);
            serilogLogger.Information("Phase: socket bind");
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

            builder.Logging.AddSerilog(serilogLogger, dispose: false);

            serilogLogger.Information("Phase: services start");
            builder.Services.AddGrpc(options => options.Interceptors.Add<DegradationWarningInterceptor>());
            builder.Services.AddSingleton<DegradationWarningInterceptor>();
            builder.Services.AddSingleton<HealthServiceImpl>();
            builder.Services.AddSingleton(new RepositoryConfiguration { Path = repo });
            var hostState = new HostState
            {
                RepositoryPath = repo,
                ImplicitStart = implicitStart,
                StartedAtUtc = DateTime.UtcNow
            };
            builder.Services.AddSingleton(hostState);
            builder.Services.AddSingleton<ServiceDegradationTracker>(_ => new ServiceDegradationTracker(hostState, repo));
            builder.Services.AddSingleton<IServiceDegradationTracker>(sp => sp.GetRequiredService<ServiceDegradationTracker>());
            builder.WebHost.ConfigureKestrel(options => { GrpcServerHelper.ConfigureUnixSocket(options, repo); });
            serilogLogger.Information("Phase: database init");
            var dbInit = DatabaseInitCoordinator.Prepare(repo, serilogLogger);
            builder.Services.AddSingleton(dbInit.Options);
            builder.Services.AddRepoIndexer(repo);

            // Search services for ExploreOrchestrator (server-side, using DuckDbDataStore directly)
            builder.Services.AddSingleton<IDocumentSearchService, DocumentSearchService>();
            builder.Services.AddSingleton<IObjectSearchService, ObjectSearchService>();
            builder.Services.AddSingleton<IExploreSearchEngine, ExploreSearchEngine>();
            // JIT object search uses local ONNX for fast JIT embeddings
            builder.Services.AddSingleton<IJitObjectSearchService>(sp =>
            {
                var store = sp.GetRequiredService<DuckDbDataStore>();
                var embeddingProvider = sp.GetKeyedService<IEmbeddingProvider>("local");
                var logger = sp.GetService<ILogger<JitObjectSearchService>>();
                return new JitObjectSearchService(store, embeddingProvider, logger);
            });
            builder.Services.AddSingleton(sp => new ExploreOrchestrator(
                sp.GetRequiredService<IExploreSearchEngine>(),
                sp.GetService<IJitObjectSearchService>(),
                sp.GetService<ILlmProvider>()
            ));

            // gRPC already configured above
            builder.Services.AddSingleton<HostMetrics>();
            // Restore persisted mounts BEFORE other hosted services start
            builder.Services.AddHostedService<MountRestorationService>();
            builder.Services.AddHostedService<IdleShutdownHostedService>();
            builder.Services.AddSingleton<InitialIndexingBarrier>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<InitialIndexingBarrier>());
            builder.Services.AddSingleton<IInitialIndexingBarrier>(sp => sp.GetRequiredService<InitialIndexingBarrier>());
            builder.Services.AddSingleton<IQueryBarrier, QueryBarrier>();
            builder.Services.AddSingleton<StatusEventAggregator>();
            builder.Services.AddHostedService<PipelineHealthPublisher>();

            var app = builder.Build();
            HostLogging.RegisterShutdown(app.Lifetime, app.Logger);
            var pidFile = new HostPidFile(repo);
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                if (!pidFile.TryWrite(Environment.ProcessId, out var error))
                {
                    app.Logger.LogWarning(error, "Failed to write host PID file at {Path}.", pidFile.FilePath);
                }
            });
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                if (!pidFile.TryDelete(out var error))
                {
                    app.Logger.LogWarning(error, "Failed to remove host PID file at {Path}.", pidFile.FilePath);
                }
            });

            await DatabaseInitCoordinator.InitializeAsync(app.Services, repo, dbInit.Report, serilogLogger, CancellationToken.None)
                .ConfigureAwait(false);

            // Always log startup info
            app.Logger.LogInformation("[Host] cwd={WorkingDirectory} repository={Repository} resolved repo={ResolvedRepository}", cwd, repository, repo);
            app.MapGrpcService<RepoQlServiceImpl>();
            app.MapGrpcService<HealthServiceImpl>();
            var health = app.Services.GetRequiredService<HealthServiceImpl>();
            health.SetStatus(string.Empty, HealthCheckResponse.Types.ServingStatus.NotServing);
            health.SetStatus("repoql.v1.RepoQL", HealthCheckResponse.Types.ServingStatus.NotServing);
            var degradationTracker = app.Services.GetRequiredService<ServiceDegradationTracker>();
            degradationTracker.AttachHealth(health);
            var barrier = app.Services.GetRequiredService<IInitialIndexingBarrier>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await barrier.InitialScanCompleted.ConfigureAwait(false);
                    health.SetStatus(string.Empty, HealthCheckResponse.Types.ServingStatus.Serving);
                    health.SetStatus("repoql.v1.RepoQL", HealthCheckResponse.Types.ServingStatus.Serving);
                    app.Logger.LogInformation("Phase: ready");
                    app.Logger.LogInformation("Host ready");
                }
                catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning(ex, "Initial indexing barrier failed; health check remains NOT_SERVING");
                }
            });
            await app.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            hostLock.Dispose();
        }
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

    private async Task TryShutdownExistingHostAsync(
        string repo,
        bool implicitStart,
        Serilog.ILogger logger,
        CancellationToken cancellationToken)
    {
        var resolvedRoot = Path.GetFullPath(repo);
        var overridePath = Environment.GetEnvironmentVariable("REPOQL_SOCKET");
        var socketPath = RepoqlSocketPathResolver.ResolvePhysical(resolvedRoot, overridePath, enableWslMapping: true);
        var report = new ExistingHostReport
        {
            SocketPath = socketPath,
            SocketExists = File.Exists(socketPath)
        };

        try
        {
            if (!report.SocketExists)
            {
                return;
            }

            var probeResult = await ProbeSocketAsync(socketPath, cancellationToken).ConfigureAwait(false);
            report.ProbeResult = probeResult.ToString();
            if (probeResult != SocketProbeResult.Active)
            {
                if (probeResult == SocketProbeResult.PlatformUnsupported)
                {
                    logger.Warning("Unix sockets not supported; treating socket as stale (path={Path}).", socketPath);
                }

                report.SocketCleanupAttempted = true;
                if (!UnixSocketTransport.TryCleanupStaleSocket(socketPath, out var cleanupError) && File.Exists(socketPath))
                {
                    report.SocketCleanupSucceeded = false;
                    report.SocketCleanupError = cleanupError?.Message;
                    throw new InvalidOperationException(
                        $"Failed to remove stale socket at {socketPath}. {cleanupError?.Message}");
                }

                report.SocketCleanupSucceeded = true;
                return;
            }

            report.ShutdownAttempted = true;
            var shutdownResult = await TryRequestShutdownAsync(socketPath, cancellationToken).ConfigureAwait(false);
            report.ShutdownSucceeded = shutdownResult.Success;
            report.ShutdownProcessId = shutdownResult.ProcessId > 0 ? shutdownResult.ProcessId : null;
            report.ShutdownError = shutdownResult.Error;
            if (!shutdownResult.Success && !string.IsNullOrWhiteSpace(shutdownResult.Error))
            {
                logger.Warning("Shutdown RPC failed ({Error}).", shutdownResult.Error);
            }

            var pidFile = new HostPidFile(repo);
            var pidFileFound = pidFile.TryRead(out var filePid);
            report.PidFileFound = pidFileFound;
            report.PidFileValue = pidFileFound ? filePid : null;
            var pid = shutdownResult.ProcessId > 0
                ? shutdownResult.ProcessId
                : (pidFileFound ? filePid : 0);

            if (shutdownResult.Success && pid > 0)
            {
                WriteStatus(implicitStart, logger, $"Detected existing RepoQL host (PID {pid}); requesting shutdown...", MarkupStyle.Warning);
                var exited = await ProcessTermination.WaitForExitAsync(pid, TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                if (exited)
                {
                    report.ProcessRunning = false;
                    pidFile.TryDelete(out _);
                    UnixSocketTransport.TryCleanupStaleSocket(socketPath, out _);
                    return;
                }
            }

            if (pid > 0)
            {
                if (RepoQlProcessInspector.TryGetRepoQlProcess(pid, out var process))
                {
                    report.ProcessRunning = true;
                    report.ProcessName = process.ProcessName;
                    report.KillAttempted = true;
                    WriteStatus(implicitStart, logger, $"Process {pid} did not exit; forcing termination.", MarkupStyle.Error);
                    var killed = await ProcessTermination.TryTerminateAsync(process, cancellationToken).ConfigureAwait(false);
                    report.KillSucceeded = killed;
                    if (!killed)
                    {
                        logger.Warning("Failed to terminate existing host (pid={Pid}).", pid);
                    }
                    else
                    {
                        pidFile.TryDelete(out _);
                        report.ProcessRunning = false;
                    }
                }
                else
                {
                    report.ProcessRunning = false;
                    logger.Warning("PID file found but process {Pid} is not RepoQL; skipping force kill.", pid);
                    pidFile.TryDelete(out _);
                }
            }
            else
            {
                report.ProcessRunning = false;
                logger.Warning("Shutdown failed and no RepoQL PID file found; skipping force kill.");
                pidFile.TryDelete(out _);
            }

            report.SocketCleanupAttempted = true;
            if (!UnixSocketTransport.TryCleanupStaleSocket(socketPath, out var finalCleanupError) && File.Exists(socketPath))
            {
                report.SocketCleanupSucceeded = false;
                report.SocketCleanupError = finalCleanupError?.Message;
                throw new InvalidOperationException(
                    $"Unable to remove stale socket at {socketPath}. {finalCleanupError?.Message}");
            }

            report.SocketCleanupSucceeded = true;
        }
        finally
        {
            HostDiagnosticsStore.TryWriteReport(repo, "existing-host.json", report);
        }
    }

    private static async Task<SocketProbeResult> ProbeSocketAsync(string socketPath, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            return SocketProbeResult.Active;
        }
        catch (PlatformNotSupportedException)
        {
            return SocketProbeResult.PlatformUnsupported;
        }
        catch (SocketException)
        {
            return SocketProbeResult.Stale;
        }
        catch (IOException)
        {
            return SocketProbeResult.Stale;
        }
    }

    private static async Task<ShutdownAttemptResult> TryRequestShutdownAsync(string socketPath, CancellationToken cancellationToken)
    {
        try
        {
            var transport = new UnixSocketTransport(socketPath);
            using var channel = GrpcChannel.ForAddress(UnixSocketTransport.Address, new GrpcChannelOptions
            {
                HttpHandler = transport.CreateHandler(),
                Credentials = ChannelCredentials.Insecure
            });

            var client = new RepoQL.Contracts.RepoQL.RepoQLClient(channel);
            var response = await client.ShutdownHostAsync(
                new ShutdownHostRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ShutdownAttemptResult(true, response.ProcessId, null);
        }
        catch (RpcException ex)
        {
            return new ShutdownAttemptResult(false, 0, $"{ex.StatusCode}: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            return new ShutdownAttemptResult(false, 0, ex.Message);
        }
    }


    private void WriteStatus(bool implicitStart, Serilog.ILogger logger, string message, MarkupStyle style)
    {
        logger.Information(message);
        if (implicitStart)
            return;

        var color = style switch
        {
            MarkupStyle.Warning => "yellow",
            MarkupStyle.Error => "red",
            _ => "white"
        };

        console.MarkupLine($"[{color}]{Markup.Escape(message)}[/]");
    }

    private enum SocketProbeResult
    {
        Active,
        Stale,
        PlatformUnsupported
    }

    private enum MarkupStyle
    {
        Info,
        Warning,
        Error
    }

    private readonly record struct ShutdownAttemptResult(bool Success, int ProcessId, string? Error);

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

            using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

    private static async Task<HostLock?> WaitForHostLockAsync(
        string repo,
        TimeSpan timeout,
        bool implicitStart,
        Serilog.ILogger logger,
        CancellationToken cancellationToken)
    {
        var lockPath = HostLock.GetLockPath(repo);
        var sw = Stopwatch.StartNew();
        var loggedWait = false;

        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hostLock = HostLock.TryAcquire(repo, out var failure, out var error);
            if (hostLock is not null)
            {
                logger.Information("Host lock acquired (path={Path})", lockPath);
                return hostLock;
            }

            if (failure == HostLockFailure.Unauthorized || failure == HostLockFailure.Error)
            {
                throw new InvalidOperationException($"Failed to acquire host lock at {lockPath}.", error);
            }

            if (implicitStart)
            {
                logger.Information("Host lock held by another process (path={Path}); exiting implicit host start.", lockPath);
                return null;
            }

            if (!loggedWait)
            {
                logger.Information("Host lock held by another process; waiting for release (path={Path}).", lockPath);
                loggedWait = true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        logger.Warning("Host lock still held after {TimeoutSeconds}s (path={Path})", timeout.TotalSeconds, lockPath);
        return null;
    }

}
