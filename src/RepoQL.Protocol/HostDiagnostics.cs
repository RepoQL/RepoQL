namespace RepoQL.Protocol;

/// <summary>
/// Diagnostic information about the RepoQL host process.
/// Used for debugging connection and startup issues.
/// </summary>
public sealed record HostDiagnostics(
    /// <summary>Recent stderr output from the host (last ~50 lines).</summary>
    IReadOnlyList<string> StderrTail,
    /// <summary>Exit code if the host process has exited.</summary>
    int? ExitCode,
    /// <summary>Working directory the host was launched in.</summary>
    string? WorkingDirectory,
    /// <summary>Path to the executable that was launched.</summary>
    string? ExecutablePath,
    /// <summary>When the host was launched.</summary>
    DateTime? LaunchTime,
    /// <summary>Process ID of the host, if still tracked.</summary>
    int? ProcessId,
    /// <summary>Whether the host process has exited.</summary>
    bool? HasExited
);
