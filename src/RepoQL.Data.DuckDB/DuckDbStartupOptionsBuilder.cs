using System.Text.RegularExpressions;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Build validated DuckDB startup options from environment and defaults.
/// Complexity: Normalizes input and tracks invalid overrides without throwing.
/// </summary>
public static class DuckDbStartupOptionsBuilder
{
    private static readonly Regex MemoryLimitPattern = new(
        "^\\s*\\d+\\s*(B|KB|MB|GB|TB)?\\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static DuckDbStartupOptions Build(string? databasePath)
    {
        var invalid = new List<DuckDbEnvironmentIssue>();
        var (defaultThreads, defaultMemory) = DuckDbDefaults.GetOptimalConfig();

        var memoryLimit = ResolveMemoryLimit(defaultMemory, invalid);
        var threads = ResolveThreads(defaultThreads, invalid);
        var tempDirectory = ResolveTempDirectory(databasePath);
        var readPoolSize = ResolveReadPoolSize(invalid);

        return new DuckDbStartupOptions(
            memoryLimit,
            threads,
            tempDirectory,
            readPoolSize,
            invalid);
    }

    private static string ResolveMemoryLimit(string defaultMemory, List<DuckDbEnvironmentIssue> invalid)
    {
        var raw = Environment.GetEnvironmentVariable("DUCKDB_MEMORY_LIMIT");
        if (string.IsNullOrWhiteSpace(raw))
            return defaultMemory;

        var normalized = raw.Trim().ToUpperInvariant();
        if (!MemoryLimitPattern.IsMatch(normalized))
        {
            invalid.Add(new DuckDbEnvironmentIssue("DUCKDB_MEMORY_LIMIT", raw, "Invalid memory limit format."));
            return defaultMemory;
        }

        return normalized;
    }

    private static int ResolveThreads(string defaultThreads, List<DuckDbEnvironmentIssue> invalid)
    {
        var raw = Environment.GetEnvironmentVariable("DUCKDB_THREADS");
        if (string.IsNullOrWhiteSpace(raw))
            return int.Parse(defaultThreads);

        if (!int.TryParse(raw.Trim(), out var parsed) || parsed <= 0)
        {
            invalid.Add(new DuckDbEnvironmentIssue("DUCKDB_THREADS", raw, "Thread count must be positive."));
            return int.Parse(defaultThreads);
        }

        return parsed;
    }

    private static int ResolveReadPoolSize(List<DuckDbEnvironmentIssue> invalid)
    {
        var raw = Environment.GetEnvironmentVariable("DUCKDB_READ_POOL_SIZE");
        if (string.IsNullOrWhiteSpace(raw))
            return 2;

        if (!int.TryParse(raw.Trim(), out var parsed) || parsed < 0)
        {
            invalid.Add(new DuckDbEnvironmentIssue("DUCKDB_READ_POOL_SIZE", raw, "Read pool size must be non-negative."));
            return 2;
        }

        return parsed;
    }

    private static string ResolveTempDirectory(string? databasePath)
    {
        var env = Environment.GetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var baseDir = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(".repoql")
            : Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";

        return Path.Combine(baseDir, "temp");
    }
}
