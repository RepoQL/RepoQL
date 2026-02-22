namespace RepoQL.Protocol;

/// <summary>
/// Diagnostic information about the RepoQL host process.
/// Used for debugging connection and startup issues.
/// </summary>
/// <param name="StderrTail">Recent stderr output from the host (last ~50 lines).</param>
/// <param name="ExitCode">Exit code if the host process has exited.</param>
/// <param name="WorkingDirectory">Working directory the host was launched in.</param>
/// <param name="ExecutablePath">Path to the executable that was launched.</param>
/// <param name="LaunchTime">When the host was launched.</param>
/// <param name="ProcessId">Process ID of the host, if still tracked.</param>
/// <param name="HasExited">Whether the host process has exited.</param>
public sealed record HostDiagnostics(
    IReadOnlyList<string> StderrTail,
    int? ExitCode,
    string? WorkingDirectory,
    string? ExecutablePath,
    DateTime? LaunchTime,
    int? ProcessId,
    bool? HasExited
);
