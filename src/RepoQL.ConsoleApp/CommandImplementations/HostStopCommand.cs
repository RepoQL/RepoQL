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
/// Purpose: Stop the gRPC host for the current repository without relaunching it.
/// Complexity: Runs diagnostics-first shutdown with graceful RPC, PID fallback,
/// best-effort local cleanup, and verification that the host is no longer serving.
/// </summary>
[CommandClass]
internal sealed class HostStopCommand
{
    private static readonly TimeSpan ShutdownRpcDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostStopOperations _operations;

    public HostStopCommand(RepoQlClientProvider clientProvider)
        : this(new HostStopOperations(clientProvider))
    {
    }

    internal HostStopCommand(IHostStopOperations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    [Command("host.stop", Description = "Stop the repository host")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        DiagnosticReport initial;

        try
        {
            initial = await _operations.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CommandResult.Error(BuildLocalStateDiscoveryError(ex));
        }

        var socketConnectable = initial.SocketConnectable;
        var hostProcessId = initial.HostProcessId;
        var socketPath = initial.SocketPath;
        var hostRunning = initial.HostRunning;

        var shutdownAttempt = ShutdownAttempt.NotAttempted();
        if (socketConnectable == true && !string.IsNullOrWhiteSpace(socketPath))
        {
            shutdownAttempt = await _operations.TryShutdownHostAsync(socketPath!, cancel).ConfigureAwait(false);
        }

        var termination = await StopPreviousProcessAsync(hostProcessId, cancel).ConfigureAwait(false);
        var socketCleanup = _operations.CleanupSocket(socketPath);
        var pidCleanup = _operations.CleanupPidFile(initial.RepoRoot);
        await _operations.ResetClientStateAsync().ConfigureAwait(false);

        DiagnosticReport verification;
        try
        {
            verification = await _operations.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
        }
        catch (Exception verificationEx)
        {
            return CommandResult.Error(BuildVerificationFailedError(socketPath, verificationEx));
        }

        var hostStopped = verification.SocketConnectable != true && verification.HostRunning != true;
        if (hostStopped)
        {
            return CommandResult.Success(BuildSuccessMessage(
                sw.Elapsed,
                termination,
                shutdownAttempt,
                hostRunning,
                socketCleanup,
                pidCleanup));
        }

        if (termination.Failed)
            return CommandResult.Error(BuildProcessTerminationFailedError(termination));

        if (socketCleanup.Failed)
            return CommandResult.Error(BuildSocketCleanupFailedError(socketCleanup.Path, socketCleanup.Error));

        return CommandResult.Error(BuildHostStillRunningError(verification, shutdownAttempt, termination));
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

    private static string BuildSuccessMessage(
        TimeSpan elapsed,
        TerminationResult termination,
        ShutdownAttempt shutdownAttempt,
        bool? initialHostRunning,
        CleanupResult socketCleanup,
        CleanupResult pidCleanup)
    {
        var previousPidText = termination.Pid?.ToString() ?? "unknown";
        var method = termination.Method;
        var initialState = initialHostRunning.HasValue ? initialHostRunning.Value.ToString().ToLowerInvariant() : "unknown";

        var sb = new StringBuilder();
        if (initialHostRunning == true || termination.Pid.HasValue || shutdownAttempt.Attempted)
            sb.AppendLine($"Host stopped in {elapsed.TotalSeconds:F1}s (previous PID {previousPidText} {method}).");
        else
            sb.AppendLine($"Host already stopped ({elapsed.TotalSeconds:F1}s).");

        sb.AppendLine($"Initial host_running: {initialState}");

        if (shutdownAttempt.Attempted && !shutdownAttempt.Succeeded && !string.IsNullOrWhiteSpace(shutdownAttempt.Error))
            sb.AppendLine($"Shutdown RPC fallback: {shutdownAttempt.Error}");

        if (socketCleanup.Failed)
            sb.AppendLine($"Cleanup warning (socket): {socketCleanup.Error}");
        if (pidCleanup.Failed)
            sb.AppendLine($"Cleanup warning (pid): {pidCleanup.Error}");

        return sb.ToString().TrimEnd();
    }

    private static string BuildLocalStateDiscoveryError(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("::host.stop failed: local state discovery failed.");
        sb.AppendLine($"  error: {ex.Message}");
        sb.Append("  manual: Run ::diagnostics.fast and retry.");
        return sb.ToString();
    }

    private static string BuildVerificationFailedError(string? socketPath, Exception ex)
    {
        var path = string.IsNullOrWhiteSpace(socketPath) ? "<unknown>" : socketPath;
        var sb = new StringBuilder();
        sb.AppendLine("::host.stop failed: verification failed after shutdown.");
        sb.AppendLine($"  socket: {path}");
        sb.AppendLine($"  error: {ex.Message}");
        sb.Append("  manual: Run ::diagnostics.fast to inspect current state.");
        return sb.ToString();
    }

    private static string BuildProcessTerminationFailedError(TerminationResult termination)
    {
        var pidText = termination.Pid?.ToString() ?? "unknown";
        var processName = string.IsNullOrWhiteSpace(termination.ProcessName)
            ? "unknown"
            : termination.ProcessName;

        var sb = new StringBuilder();
        sb.AppendLine("::host.stop failed: process termination failed.");
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
        sb.AppendLine("::host.stop failed: socket cleanup failed.");
        sb.AppendLine($"  socket: {path}");
        sb.AppendLine($"  error: {reason}");
        sb.Append($"  manual: rm {path}");
        return sb.ToString();
    }

    private static string BuildHostStillRunningError(
        DiagnosticReport verification,
        ShutdownAttempt shutdownAttempt,
        TerminationResult termination)
    {
        var socketPath = string.IsNullOrWhiteSpace(verification.SocketPath)
            ? "<unknown>"
            : verification.SocketPath;
        var sb = new StringBuilder();
        sb.AppendLine("::host.stop failed: host still appears to be running.");
        sb.AppendLine($"  socket: {socketPath}");
        sb.AppendLine($"  host_running: {(verification.HostRunning.HasValue ? verification.HostRunning.Value.ToString().ToLowerInvariant() : "unknown")}");
        sb.AppendLine($"  socket_connectable: {(verification.SocketConnectable.HasValue ? verification.SocketConnectable.Value.ToString().ToLowerInvariant() : "unknown")}");

        if (shutdownAttempt.Attempted)
            sb.AppendLine($"  shutdown_rpc: {(shutdownAttempt.Succeeded ? "succeeded" : shutdownAttempt.Error ?? "failed")}");

        if (termination.Pid.HasValue)
            sb.AppendLine($"  previous_pid: {termination.Pid} ({termination.Method})");

        var stderrLines = GetHostStderrLines(verification).TakeLast(5).ToList();
        if (stderrLines.Count > 0)
        {
            sb.AppendLine("  last_stderr:");
            foreach (var line in stderrLines)
                sb.AppendLine($"  - {line}");
        }

        sb.Append("  manual: Run ::diagnostics.fast and kill the reported process if needed.");
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

    internal interface IHostStopOperations
    {
        Task<DiagnosticReport> CollectDiagnosticsAsync(DiagnosticCollectionMode mode, CancellationToken ct);
        Task<ShutdownAttempt> TryShutdownHostAsync(string socketPath, CancellationToken ct);
        Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct);
        ProcessInspection InspectProcess(int pid);
        Task<bool> TryTerminateRepoQlProcessAsync(int pid, CancellationToken ct);
        CleanupResult CleanupSocket(string? socketPath);
        CleanupResult CleanupPidFile(string? repoRoot);
        Task ResetClientStateAsync();
    }

    private sealed class HostStopOperations(RepoQlClientProvider clientProvider) : IHostStopOperations
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
            // PID is now embedded in the lock file — no separate file to clean up.
            // Lock file lifetime is managed by the FileStream in HostLock.
            return CleanupResult.Success(null);
        }

        public async Task ResetClientStateAsync()
        {
            await clientProvider.DisposeAsync().ConfigureAwait(false);
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
}
