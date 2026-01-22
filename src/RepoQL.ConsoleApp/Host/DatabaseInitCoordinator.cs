using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Coordinate database initialization, validation, and recovery before host startup.
/// Complexity: Aggregates environment validation, lock detection, and recovery paths into a single flow.
/// </summary>
internal static class DatabaseInitCoordinator
{
    public static DatabaseInitPreparation Prepare(string repoRoot, Serilog.ILogger logger)
    {
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repoRoot);
        var dbPath = Path.Combine(repoqlDir, "index.duckdb");
        var report = new DatabaseInitReport { Path = dbPath };

        if (File.Exists(dbPath))
        {
            report.Existed = true;
            report.SizeBytes = new FileInfo(dbPath).Length;
        }

        report.DiskFreeBytes = TryGetDiskFreeBytes(dbPath);

        var options = DuckDbStartupOptionsBuilder.Build(dbPath);
        report.EnvVarsValidated = true;
        foreach (var issue in options.InvalidEnvironmentVariables)
        {
            report.InvalidEnvVars.Add(new DatabaseEnvVarIssue(issue.Name, issue.Value, issue.Error));
        }

        var (tempOk, tempPath, tempError) = ValidateTempDirectory(options.TempDirectory);
        report.TempDirPath = tempPath;
        report.TempDirWritable = tempOk;
        report.TempDirError = tempError;

        if (!tempOk)
        {
            HostDiagnosticsStore.TryWriteReport(repoRoot, "database-init.json", report);
            throw new InvalidOperationException($"DuckDB temp directory is not writable: {tempError}");
        }

        var normalizedOptions = options with { TempDirectory = tempPath! };
        return new DatabaseInitPreparation(normalizedOptions, report);
    }

    public static async Task InitializeAsync(
        IServiceProvider services,
        string repoRoot,
        DatabaseInitReport report,
        Serilog.ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await InitializeInternalAsync(services, report, logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            HostDiagnosticsStore.TryWriteReport(repoRoot, "database-init.json", report);
        }
    }

    private static async Task InitializeInternalAsync(
        IServiceProvider services,
        DatabaseInitReport report,
        Serilog.ILogger logger,
        CancellationToken cancellationToken)
    {
        var recoveryAttempted = false;

        while (true)
        {
            report.OpenAttempted = true;
            DuckDbDataStore? store = null;

            try
            {
                store = services.GetRequiredService<DuckDbDataStore>();
                store.InitializeSchema();
                report.OpenSucceeded = true;
                report.OpenError = null;
                report.OpenErrorType = null;
                return;
            }
            catch (Exception ex)
            {
                report.OpenSucceeded = false;
                report.OpenError = ex.Message;
                var errorType = DatabaseOpenErrorClassifier.Classify(ex);
                report.OpenErrorType = ToReportValue(errorType);

                if (errorType == DatabaseOpenErrorType.Locked)
                {
                    report.LockHolder = DatabaseLockInspector.TryGetLockHolder(report.Path, logger);
                    if (report.LockHolder is { } lockHolder &&
                        RepoQlProcessInspector.TryGetRepoQlProcess(lockHolder.ProcessId, out var process))
                    {
                        report.RecoveryOffered = true;
                        logger.Warning("Database lock held by RepoQL process {Pid}; attempting termination.", lockHolder.ProcessId);
                        var killed = await ProcessTermination.TryTerminateAsync(process, cancellationToken).ConfigureAwait(false);
                        if (killed)
                        {
                            continue;
                        }
                    }

                    throw new InvalidOperationException(BuildLockMessage(report));
                }

                if ((errorType == DatabaseOpenErrorType.Corrupted || errorType == DatabaseOpenErrorType.SchemaMismatch) &&
                    !recoveryAttempted)
                {
                    report.RecoveryOffered = true;
                    recoveryAttempted = true;
                    logger.Warning("Database open failed ({ErrorType}); rebuilding database.", errorType);

                    if (store is not null)
                    {
                        store.RecreateDatabase();
                        report.OpenSucceeded = true;
                        report.OpenError = null;
                        report.OpenErrorType = null;
                        return;
                    }

                    DeleteDatabaseFiles(report.Path);
                    continue;
                }

                if (errorType == DatabaseOpenErrorType.Permission)
                    throw new InvalidOperationException($"Database access denied at {report.Path}.", ex);

                if (errorType == DatabaseOpenErrorType.DiskFull)
                    throw new InvalidOperationException($"Insufficient disk space to open {report.Path}.", ex);

                throw new InvalidOperationException($"Failed to open database at {report.Path}.", ex);
            }
        }
    }

    private static string BuildLockMessage(DatabaseInitReport report)
    {
        if (report.LockHolder is null)
        {
            return $"Database at {report.Path} is locked by an unknown process.";
        }

        return $"Database at {report.Path} is locked by PID {report.LockHolder.ProcessId} ({report.LockHolder.ProcessName ?? "unknown"}).";
    }

    private static (bool success, string? normalizedPath, string? error) ValidateTempDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, null, "Temp directory is empty.");

        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && IsUncPath(fullPath))
            return (false, fullPath, "Temp directory must be on a local drive.");

        try
        {
            Directory.CreateDirectory(fullPath);
            var probe = Path.Combine(fullPath, $"repoql-temp-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return (true, fullPath.Replace('\\', '/'), null);
        }
        catch (Exception ex)
        {
            return (false, fullPath, ex.Message);
        }
    }

    private static bool IsUncPath(string path)
    {
        return path.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        var walPath = path + ".wal";
        if (File.Exists(walPath))
            File.Delete(walPath);
    }

    private static long? TryGetDiskFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ToReportValue(DatabaseOpenErrorType errorType)
        => errorType switch
        {
            DatabaseOpenErrorType.SchemaMismatch => "schema",
            DatabaseOpenErrorType.DiskFull => "disk_full",
            _ => errorType.ToString().ToLowerInvariant()
        };
}

internal sealed record DatabaseInitPreparation(
    DuckDbStartupOptions Options,
    DatabaseInitReport Report);
