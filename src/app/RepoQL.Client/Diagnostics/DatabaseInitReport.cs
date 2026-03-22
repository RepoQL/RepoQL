using System.Text;

namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Purpose: Capture database initialization context and recovery attempts for diagnostics.
/// Complexity: Stores open, validation, and recovery outcomes without requiring all fields.
/// </summary>
internal sealed class DatabaseInitReport
{
    public required string Path { get; init; }
    public bool Existed { get; set; }
    public long? SizeBytes { get; set; }
    public bool EnvVarsValidated { get; set; }
    public List<DatabaseEnvVarIssue> InvalidEnvVars { get; } = [];
    public string? TempDirPath { get; set; }
    public bool TempDirWritable { get; set; }
    public string? TempDirError { get; set; }
    public bool OpenAttempted { get; set; }
    public bool OpenSucceeded { get; set; }
    public string? OpenError { get; set; }
    public string? OpenErrorType { get; set; }
    public ProcessInfo? LockHolder { get; set; }
    public long? DiskFreeBytes { get; set; }
    public bool RecoveryOffered { get; set; }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Database init:");
        builder.AppendLine($"  path: {Path}");
        builder.AppendLine($"  existed: {Existed}");
        if (SizeBytes.HasValue)
            builder.AppendLine($"  size_bytes: {SizeBytes.Value}");
        builder.AppendLine($"  env_validated: {EnvVarsValidated}");
        if (InvalidEnvVars.Count > 0)
        {
            builder.AppendLine($"  invalid_env_vars: {InvalidEnvVars.Count}");
            foreach (var issue in InvalidEnvVars)
            {
                builder.AppendLine($"    {issue.Name}: {issue.Value} ({issue.Error})");
            }
        }
        if (!string.IsNullOrWhiteSpace(TempDirPath))
            builder.AppendLine($"  temp_dir: {TempDirPath}");
        builder.AppendLine($"  temp_dir_writable: {TempDirWritable}");
        if (!string.IsNullOrWhiteSpace(TempDirError))
            builder.AppendLine($"  temp_dir_error: {TempDirError}");
        builder.AppendLine($"  open_attempted: {OpenAttempted}");
        builder.AppendLine($"  open_succeeded: {OpenSucceeded}");
        if (!string.IsNullOrWhiteSpace(OpenErrorType))
            builder.AppendLine($"  open_error_type: {OpenErrorType}");
        if (!string.IsNullOrWhiteSpace(OpenError))
            builder.AppendLine($"  open_error: {OpenError}");
        if (LockHolder is not null)
            builder.AppendLine($"  lock_holder: PID {LockHolder.ProcessId} ({LockHolder.ProcessName ?? "unknown"})");
        if (DiskFreeBytes.HasValue)
            builder.AppendLine($"  disk_free_bytes: {DiskFreeBytes.Value}");
        builder.AppendLine($"  recovery_offered: {RecoveryOffered}");
        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Purpose: Record invalid DuckDB environment variable values in diagnostics.
/// Complexity: Simple data carrier for configuration validation.
/// </summary>
internal sealed record DatabaseEnvVarIssue(string Name, string? Value, string Error);
