namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Represent validated DuckDB startup configuration for host initialization.
/// Complexity: Carries resolved values and any invalid environment overrides for reporting.
/// </summary>
public sealed record DuckDbStartupOptions(
    string MemoryLimit,
    int Threads,
    string TempDirectory,
    int ReadPoolSize,
    IReadOnlyList<DuckDbEnvironmentIssue> InvalidEnvironmentVariables);

/// <summary>
/// Purpose: Capture invalid DuckDB environment variable values.
/// Complexity: Simple data carrier for diagnostics.
/// </summary>
public sealed record DuckDbEnvironmentIssue(string Name, string? Value, string Error);
