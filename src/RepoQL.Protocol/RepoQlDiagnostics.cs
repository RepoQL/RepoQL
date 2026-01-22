using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Carry structured connection diagnostics so callers can render actionable failures.
/// Complexity: Aggregates partial environment and host details without requiring every field to be populated.
/// </summary>
public sealed record RepoQlDiagnostics(
    string? RepoRoot,
    string? SocketPath,
    string? ChannelState,
    string? HealthStatus,
    bool HealthWatchFaulted,
    HostDiagnostics? Host,
    bool RecoveryAttempted,
    bool CircuitBreakerOpen,
    int CircuitBreakerFailures,
    TimeSpan CircuitBreakerWindow)
{
    public static RepoQlDiagnostics Empty { get; } = new(
        RepoRoot: null,
        SocketPath: null,
        ChannelState: null,
        HealthStatus: null,
        HealthWatchFaulted: false,
        Host: null,
        RecoveryAttempted: false,
        CircuitBreakerOpen: false,
        CircuitBreakerFailures: 0,
        CircuitBreakerWindow: TimeSpan.Zero);

    [DoesNotReturn]
    public void Throw(string message, Exception? innerException = null)
        => throw new RepoQlDiagnosticsException(message, this, innerException);

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.AppendLine("RepoQL diagnostics:");
        builder.AppendLine($"  repo: {RepoRoot ?? "<unknown>"}");
        builder.AppendLine($"  socket: {SocketPath ?? "<unknown>"}");
        builder.AppendLine($"  channel: {ChannelState ?? "<unknown>"}");
        builder.AppendLine($"  health: {HealthStatus ?? "<unknown>"}");
        builder.AppendLine($"  watch_faulted: {HealthWatchFaulted}");
        builder.AppendLine($"  recovery_attempted: {RecoveryAttempted}");
        builder.AppendLine($"  circuit_open: {CircuitBreakerOpen}");
        builder.AppendLine($"  circuit_failures: {CircuitBreakerFailures}");
        builder.AppendLine($"  circuit_window: {CircuitBreakerWindow.TotalMinutes:N0}m");

        if (Host is { } host)
        {
            builder.AppendLine($"  host_pid: {host.ProcessId?.ToString() ?? "<unknown>"}");
            builder.AppendLine($"  host_exit: {(host.HasExited.HasValue ? host.HasExited.Value.ToString() : "<unknown>")}");
            if (host.ExitCode.HasValue)
                builder.AppendLine($"  host_exit_code: {host.ExitCode.Value}");
            if (!string.IsNullOrWhiteSpace(host.ExecutablePath))
                builder.AppendLine($"  host_exe: {host.ExecutablePath}");
            if (!string.IsNullOrWhiteSpace(host.WorkingDirectory))
                builder.AppendLine($"  host_cwd: {host.WorkingDirectory}");
            if (host.LaunchTime.HasValue)
                builder.AppendLine($"  host_started: {host.LaunchTime:O}");
            if (host.StderrTail.Count > 0)
            {
                builder.AppendLine("  host_stderr:");
                foreach (var line in host.StderrTail)
                    builder.AppendLine($"    {line}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
