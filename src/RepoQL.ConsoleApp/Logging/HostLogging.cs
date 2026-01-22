using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using Serilog;
using Serilog.Events;

namespace RepoQL.ConsoleApp.Logging;

/// <summary>
/// Purpose: Centralize host file logging setup and lifecycle hooks so startup code uses one consistent logging surface.
/// Complexity: Encapsulates Serilog configuration and crash-handling registration to keep Serve orchestration readable.
/// </summary>
internal static class HostLogging
{
    private static int _crashLoggingRegistered;

    public static (Serilog.Core.Logger Logger, string LogPath) Initialize(string repoRoot)
    {
        var logPath = ResolveLogPath(repoRoot);
        var logger = CreateLogger(logPath);
        Log.Logger = logger;
        RegisterCrashLogging();
        return (logger, logPath);
    }

    public static void RegisterShutdown(IHostApplicationLifetime lifetime, Microsoft.Extensions.Logging.ILogger logger)
    {
        lifetime.ApplicationStopping.Register(() => logger.LogInformation("Host shutting down"));
        lifetime.ApplicationStopped.Register(Flush);
    }

    public static string GetHostVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    public static void Flush()
    {
        Log.CloseAndFlush();
    }

    private static string ResolveLogPath(string repoRoot)
    {
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repoRoot);
        return Path.Combine(repoqlDir, "host.log");
    }

    private static Serilog.Core.Logger CreateLogger(string logPath)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.WithProcessId()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: 1_000_000,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 2,
                shared: true,
                buffered: false,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} PID {ProcessId} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static void RegisterCrashLogging()
    {
        if (Interlocked.Exchange(ref _crashLoggingRegistered, 1) == 1)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Logger.Error(ex, "Host crashed with unhandled exception");
            }
            else
            {
                Log.Logger.Error("Host crashed with unhandled exception: {ExceptionObject}", args.ExceptionObject);
            }

            Log.CloseAndFlush();
        };
    }
}
