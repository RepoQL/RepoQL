using System.Diagnostics;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Protocol;
using RepoQL.Protocol.Transport;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Restart the gRPC host without changing the active repository.
/// Complexity: Runs diagnostics-first restart with graceful shutdown, PID-based termination,
/// local cleanup, relaunch, and verification-based escalation.
/// </summary>
[CommandClass]
internal sealed class HostRestartCommand
{
    private static readonly TimeSpan ShutdownRpcDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostRestartOperations _operations;

    public HostRestartCommand(RepoQlClientProvider clientProvider)
        : this(new HostRestartOperations(clientProvider))
    {
    }

    internal HostRestartCommand(IHostRestartOperations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    [Command("host.restart", Description = "Restart the repository host")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        DiagnosticReport initial;

        try
        {
            // Local state discovery must be first. No GetClientAsync calls before this.
            initial = await _operations.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CommandResult.Error(BuildLocalStateDiscoveryError(ex));
        }

        var socketConnectable = initial.SocketConnectable;
        var hostProcessId = initial.HostProcessId;
        var socketPath = initial.SocketPath;
        var dbLocked = initial.DbLocked;
        var dbLockHolderName = initial.DbLockHolderName;
        var hostRunning = initial.HostRunning;

        var shutdownAttempt = ShutdownAttempt.NotAttempted();
        if (socketConnectable == true && !string.IsNullOrWhiteSpace(socketPath))
        {
            shutdownAttempt = await _operations.TryShutdownHostAsync(socketPath!, cancel).ConfigureAwait(false);
        }

        var termination = await StopPreviousProcessAsync(hostProcessId, cancel).ConfigureAwait(false);

        // Best-effort local cleanup before launch.
        var socketCleanup = _operations.CleanupSocket(socketPath);
        var pidCleanup = _operations.CleanupPidFile(initial.RepoRoot);

        if (TryGetExternalLock(dbLocked, dbLockHolderName, initial.DbLockHolderPid, out var lockHolder))
            return CommandResult.Error(BuildDatabaseLockedError(lockHolder.Name, lockHolder.Pid));

        Exception? launchError = null;
        try
        {
            await _operations.ResetClientStateAsync().ConfigureAwait(false);
            await _operations.TriggerLaunchAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            launchError = ex;
        }

        DiagnosticReport verification;
        try
        {
            verification = await _operations.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
        }
        catch (Exception verificationEx)
        {
            return CommandResult.Error(BuildHostDidNotStartError(
                socketPath,
                initial,
                launchError,
                verificationEx));
        }

        var verdict = ExtractVerdict(verification);
        if (verification.SocketConnectable == true
            && (string.Equals(verdict, "OK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(verdict, "STARTING", StringComparison.OrdinalIgnoreCase)))
        {
            return CommandResult.Success(BuildSuccessMessage(
                sw.Elapsed,
                termination,
                shutdownAttempt,
                verification,
                verdict,
                hostRunning,
                socketCleanup,
                pidCleanup));
        }

        if (TryGetExternalLock(verification.DbLocked, verification.DbLockHolderName, verification.DbLockHolderPid, out var verificationLockHolder))
            return CommandResult.Error(BuildDatabaseLockedError(verificationLockHolder.Name, verificationLockHolder.Pid));

        if (termination.Failed)
            return CommandResult.Error(BuildProcessTerminationFailedError(termination));

        if (socketCleanup.Failed)
            return CommandResult.Error(BuildSocketCleanupFailedError(socketCleanup.Path, socketCleanup.Error));

        if (verification.SocketBindSucceeded == false)
            return CommandResult.Error(BuildSocketBindFailedError(verification));

        return CommandResult.Error(BuildHostDidNotStartError(
            verification.SocketPath ?? socketPath,
            verification,
            launchError));
    }

    private async Task<TerminationResult> StopPreviousProcessAsync(int? pid, CancellationToken cancel)
    {
        if (!pid.HasValue || pid.Value <= 0)
            return TerminationResult.NotApplicable();

        var inspection = _operations.InspectProcess(pid.Value);
        if (!inspection.IsRepoQl)
            return TerminationResult.NotApplicable(pid.Value);

        var exited = await _operations.WaitForExitAsync(pid.Value, ProcessExitTimeout, cancel).ConfigureAwait(false);
        if (exited)
            return TerminationResult.Stopped(pid.Value, inspection.ProcessName);

        var killed = await _operations.TryTerminateRepoQlProcessAsync(pid.Value, cancel).ConfigureAwait(false);
        if (killed)
            return TerminationResult.Killed(pid.Value, inspection.ProcessName);

        return TerminationResult.FromFailure(pid.Value, inspection.ProcessName);
    }

    private static bool TryGetExternalLock(bool? dbLocked, string? dbLockHolderName, int? dbLockHolderPid, out LockHolder holder)
    {
        holder = default;
        if (dbLocked != true)
            return false;

        var name = string.IsNullOrWhiteSpace(dbLockHolderName)
            ? "unknown"
            : dbLockHolderName.Trim();
        if (name.Contains("repoql", StringComparison.OrdinalIgnoreCase))
            return false;

        holder = new LockHolder(name, dbLockHolderPid);
        return true;
    }

    private static string BuildSuccessMessage(
        TimeSpan elapsed,
        TerminationResult termination,
        ShutdownAttempt shutdownAttempt,
        DiagnosticReport verification,
        string verdict,
        bool? initialHostRunning,
        CleanupResult socketCleanup,
        CleanupResult pidCleanup)
    {
        var previousPidText = termination.Pid?.ToString() ?? "unknown";
        var newPidText = verification.HostProcessId?.ToString() ?? "unknown";
        var method = termination.Method;

        var sb = new StringBuilder();
        sb.AppendLine(
            $"Host restarted in {elapsed.TotalSeconds:F1}s (previous PID {previousPidText} {method}, new PID {newPidText}, verdict {verdict}).");
        sb.AppendLine($"Initial host_running: {(initialHostRunning.HasValue ? initialHostRunning.Value.ToString().ToLowerInvariant() : "unknown")}");

        if (shutdownAttempt.Attempted && !shutdownAttempt.Succeeded && !string.IsNullOrWhiteSpace(shutdownAttempt.Error))
            sb.AppendLine($"Shutdown RPC fallback: {shutdownAttempt.Error}");

        if (socketCleanup.Failed)
            sb.AppendLine($"Cleanup warning (socket): {socketCleanup.Error}");
        if (pidCleanup.Failed)
            sb.AppendLine($"Cleanup warning (pid): {pidCleanup.Error}");

        var stderrLines = GetHostStderrLines(verification).TakeLast(5).ToList();
        if (stderrLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Startup logs:");
            foreach (var line in stderrLines)
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildLocalStateDiscoveryError(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("::host.restart failed: local state discovery failed.");
        sb.AppendLine($"  error: {ex.Message}");
        sb.Append("  manual: Run ::diagnostics[fast] and retry.");
        return sb.ToString();
    }

    private static string BuildProcessTerminationFailedError(TerminationResult termination)
    {
        var pidText = termination.Pid?.ToString() ?? "unknown";
        var processName = string.IsNullOrWhiteSpace(termination.ProcessName)
            ? "unknown"
            : termination.ProcessName;

        var sb = new StringBuilder();
        sb.AppendLine("::host.restart failed: process termination failed.");
        sb.AppendLine($"  pid: {pidText}");
        sb.AppendLine($"  process: {processName}");
        sb.AppendLine($"  kill_attempted: {(termination.KillAttempted ? "yes" : "no")}");
        sb.Append($"  manual: kill -9 {pidText}");
        return sb.ToString();
    }

    private static string BuildSocketCleanupFailedError(string? socketPath, string? error)
    {
        var path = string.IsNullOrWhiteSpace(socketPath) ? "<unknown>" : socketPath;
        var reason = string.IsNullOrWhiteSpace(error) ? "unknown error" : error;

        var sb = new StringBuilder();
        sb.AppendLine("::host.restart failed: socket cleanup failed.");
        sb.AppendLine($"  socket: {path}");
        sb.AppendLine($"  error: {reason}");
        sb.Append($"  manual: rm {path}");
        return sb.ToString();
    }

    private static string BuildSocketBindFailedError(DiagnosticReport verification)
    {
        var bindError = string.IsNullOrWhiteSpace(verification.SocketBindError)
            ? "unknown"
            : verification.SocketBindError;
        var socketPath = string.IsNullOrWhiteSpace(verification.SocketPath)
            ? "<unknown>"
            : verification.SocketPath;

        var sb = new StringBuilder();
        sb.AppendLine("::host.restart failed: socket bind failed after launch.");
        sb.AppendLine($"  socket: {socketPath}");
        sb.AppendLine($"  bind_error: {bindError}");
        sb.Append("  manual: Check permissions on .repoql/");
        return sb.ToString();
    }

    private static string BuildDatabaseLockedError(string holderName, int? holderPid)
    {
        var pidText = holderPid.HasValue ? $" (pid {holderPid.Value})" : string.Empty;
        var safeHolder = string.IsNullOrWhiteSpace(holderName) ? "unknown process" : holderName;

        var sb = new StringBuilder();
        sb.AppendLine("::host.restart failed: database locked by external process.");
        sb.AppendLine($"  lock_holder: {safeHolder}{pidText}");
        sb.Append($"  manual: Close {safeHolder} to release the lock");
        return sb.ToString();
    }

    private static string BuildHostDidNotStartError(
        string? socketPath,
        DiagnosticReport report,
        Exception? launchError,
        Exception? verificationError = null)
    {
        var path = string.IsNullOrWhiteSpace(socketPath) ? "<unknown>" : socketPath;
        var sb = new StringBuilder();
        sb.AppendLine("::host.restart failed: host didn't start.");
        sb.AppendLine($"  socket: {path}");
        if (launchError is not null)
            sb.AppendLine($"  launch_error: {launchError.Message}");
        if (verificationError is not null)
            sb.AppendLine($"  verification_error: {verificationError.Message}");

        var stderrLines = GetHostStderrLines(report).TakeLast(5).ToList();
        if (stderrLines.Count > 0)
        {
            sb.AppendLine("  last_stderr:");
            foreach (var line in stderrLines)
                sb.AppendLine($"  - {line}");
        }

        sb.Append("  manual: Check .repoql/host.log");
        return sb.ToString();
    }

    private static IReadOnlyList<string> GetHostStderrLines(DiagnosticReport report)
    {
        if (report.HostStderrTail.Count > 0)
            return report.HostStderrTail;

        if (string.IsNullOrWhiteSpace(report.HostStderrFromFile))
            return Array.Empty<string>();

        return report.HostStderrFromFile
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string ExtractVerdict(DiagnosticReport report)
    {
        var firstLine = report.ToString()
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
            return "UNKNOWN";

        const string prefix = "RepoQL:";
        if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "UNKNOWN";

        return firstLine[prefix.Length..].Trim();
    }

    internal interface IHostRestartOperations
    {
        Task<DiagnosticReport> CollectDiagnosticsAsync(DiagnosticCollectionMode mode, CancellationToken ct);
        Task<ShutdownAttempt> TryShutdownHostAsync(string socketPath, CancellationToken ct);
        Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct);
        ProcessInspection InspectProcess(int pid);
        Task<bool> TryTerminateRepoQlProcessAsync(int pid, CancellationToken ct);
        CleanupResult CleanupSocket(string? socketPath);
        CleanupResult CleanupPidFile(string? repoRoot);
        Task ResetClientStateAsync();
        Task TriggerLaunchAsync(CancellationToken ct);
    }

    private sealed class HostRestartOperations(RepoQlClientProvider clientProvider) : IHostRestartOperations
    {
        private readonly DiagnosticsCollector _collector = new();

        public Task<DiagnosticReport> CollectDiagnosticsAsync(DiagnosticCollectionMode mode, CancellationToken ct)
            => _collector.CollectAsync(mode, ct);

        public async Task<ShutdownAttempt> TryShutdownHostAsync(string socketPath, CancellationToken ct)
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
                    deadline: DateTime.UtcNow.Add(ShutdownRpcDeadline),
                    cancellationToken: ct).ConfigureAwait(false);

                return ShutdownAttempt.FromSuccess(response.ProcessId);
            }
            catch (RpcException ex)
            {
                return ShutdownAttempt.FromFailure($"{ex.StatusCode}: {ex.Status.Detail}");
            }
            catch (Exception ex)
            {
                return ShutdownAttempt.FromFailure(ex.Message);
            }
        }

        public Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
            => ProcessTermination.WaitForExitAsync(pid, timeout, ct);

        public ProcessInspection InspectProcess(int pid)
        {
            if (!RepoQlProcessInspector.TryGetRepoQlProcess(pid, out var process))
                return ProcessInspection.NotRepoQl();

            try
            {
                return ProcessInspection.RepoQl(process.ProcessName);
            }
            catch
            {
                return ProcessInspection.RepoQl("repoql");
            }
            finally
            {
                process.Dispose();
            }
        }

        public async Task<bool> TryTerminateRepoQlProcessAsync(int pid, CancellationToken ct)
        {
            if (!RepoQlProcessInspector.TryGetRepoQlProcess(pid, out var process))
                return true;

            return await ProcessTermination.TryTerminateAsync(process, ct).ConfigureAwait(false);
        }

        public CleanupResult CleanupSocket(string? socketPath)
        {
            if (string.IsNullOrWhiteSpace(socketPath))
                return CleanupResult.Success(null);

            var removed = UnixSocketTransport.TryCleanupStaleSocket(socketPath, out var error);
            if (removed || error is null)
                return CleanupResult.Success(socketPath);

            return CleanupResult.Failure(socketPath, error.Message);
        }

        public CleanupResult CleanupPidFile(string? repoRoot)
        {
            if (string.IsNullOrWhiteSpace(repoRoot))
                return CleanupResult.Success(null);

            var pidFile = new HostPidFile(repoRoot);
            if (pidFile.TryDelete(out var error))
                return CleanupResult.Success(pidFile.FilePath);

            return CleanupResult.Failure(pidFile.FilePath, error?.Message);
        }

        public async Task ResetClientStateAsync()
        {
            await clientProvider.DisposeAsync().ConfigureAwait(false);
        }

        public async Task TriggerLaunchAsync(CancellationToken ct)
        {
            await clientProvider.GetClientAsync(ct).ConfigureAwait(false);
        }
    }

    internal readonly record struct ShutdownAttempt(bool Attempted, bool Succeeded, int? ProcessId, string? Error)
    {
        public static ShutdownAttempt NotAttempted() => new(false, false, null, null);
        public static ShutdownAttempt FromSuccess(int? processId) => new(true, true, processId, null);
        public static ShutdownAttempt FromFailure(string? error) => new(true, false, null, error);
    }

    internal readonly record struct ProcessInspection(bool IsRepoQl, string? ProcessName)
    {
        public static ProcessInspection RepoQl(string? processName) => new(true, processName);
        public static ProcessInspection NotRepoQl() => new(false, null);
    }

    internal readonly record struct CleanupResult(string? Path, bool Failed, string? Error)
    {
        public static CleanupResult Success(string? path) => new(path, false, null);
        public static CleanupResult Failure(string? path, string? error) => new(path, true, error);
    }

    internal readonly record struct TerminationResult(
        int? Pid,
        string? ProcessName,
        bool KillAttempted,
        bool Failed,
        string Method)
    {
        public static TerminationResult NotApplicable(int? pid = null)
            => new(pid, null, false, false, "skipped");

        public static TerminationResult Stopped(int pid, string? processName)
            => new(pid, processName, false, false, "stopped");

        public static TerminationResult Killed(int pid, string? processName)
            => new(pid, processName, true, false, "killed");

        public static TerminationResult FromFailure(int pid, string? processName)
            => new(pid, processName, true, true, "failed");
    }

    private readonly record struct LockHolder(string Name, int? Pid);
}
