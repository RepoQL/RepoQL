using System.Text;

namespace RepoQL.ConsoleApp.Diagnostics;

/// <summary>
/// Purpose: Capture host shutdown and takeover observations for diagnostics.
/// Complexity: Stores partial probe/kill/cleanup results without requiring all fields.
/// </summary>
internal sealed class ExistingHostReport
{
    public required string SocketPath { get; init; }
    public bool SocketExists { get; set; }
    public string ProbeResult { get; set; } = "unknown";
    public bool ShutdownAttempted { get; set; }
    public bool ShutdownSucceeded { get; set; }
    public int? ShutdownProcessId { get; set; }
    public string? ShutdownError { get; set; }
    public bool PidFileFound { get; set; }
    public int? PidFileValue { get; set; }
    public bool ProcessRunning { get; set; }
    public string? ProcessName { get; set; }
    public bool KillAttempted { get; set; }
    public bool KillSucceeded { get; set; }
    public bool SocketCleanupAttempted { get; set; }
    public bool SocketCleanupSucceeded { get; set; }
    public string? SocketCleanupError { get; set; }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Existing host:");
        builder.AppendLine($"  socket: {SocketPath}");
        builder.AppendLine($"  socket_exists: {SocketExists}");
        builder.AppendLine($"  probe: {ProbeResult}");
        builder.AppendLine($"  shutdown_attempted: {ShutdownAttempted}");
        builder.AppendLine($"  shutdown_succeeded: {ShutdownSucceeded}");
        if (ShutdownProcessId.HasValue)
            builder.AppendLine($"  shutdown_pid: {ShutdownProcessId.Value}");
        if (!string.IsNullOrWhiteSpace(ShutdownError))
            builder.AppendLine($"  shutdown_error: {ShutdownError}");
        builder.AppendLine($"  pid_file_found: {PidFileFound}");
        if (PidFileValue.HasValue)
            builder.AppendLine($"  pid_file_value: {PidFileValue.Value}");
        builder.AppendLine($"  process_running: {ProcessRunning}");
        if (!string.IsNullOrWhiteSpace(ProcessName))
            builder.AppendLine($"  process_name: {ProcessName}");
        builder.AppendLine($"  kill_attempted: {KillAttempted}");
        builder.AppendLine($"  kill_succeeded: {KillSucceeded}");
        builder.AppendLine($"  socket_cleanup_attempted: {SocketCleanupAttempted}");
        builder.AppendLine($"  socket_cleanup_succeeded: {SocketCleanupSucceeded}");
        if (!string.IsNullOrWhiteSpace(SocketCleanupError))
            builder.AppendLine($"  socket_cleanup_error: {SocketCleanupError}");
        return builder.ToString().TrimEnd();
    }
}
