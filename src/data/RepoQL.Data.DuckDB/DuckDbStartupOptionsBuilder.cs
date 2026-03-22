using System.Text.RegularExpressions;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Build validated DuckDB startup options from resolved config and defaults.
/// Complexity: Normalizes input and tracks invalid overrides without throwing.
/// </summary>
public static class DuckDbStartupOptionsBuilder
{
    private const int DefaultReadPoolSize = 2;
    private const int MaxReadPoolSize = 4;

    private static readonly Regex MemoryLimitPattern = new(
        "^\\s*\\d+\\s*(B|KB|MB|GB|TB)?\\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static DuckDbStartupOptions Build(
        string? databasePath,
        RepoQlConfig.DuckDbSettings? duckDbSettings = null)
    {
        var invalid = new List<DuckDbEnvironmentIssue>();
        var (defaultThreads, defaultMemory) = DuckDbDefaults.GetOptimalConfig();
        duckDbSettings ??= new RepoQlConfig.DuckDbSettings();

        var memoryLimit = ResolveMemoryLimit(defaultMemory, duckDbSettings, invalid);
        var threads = ResolveThreads(defaultThreads, duckDbSettings, invalid);
        var tempDirectory = ResolveTempDirectory(databasePath, duckDbSettings);
        var readPoolSize = ResolveReadPoolSize(duckDbSettings, invalid);

        return new DuckDbStartupOptions(
            memoryLimit,
            threads,
            tempDirectory,
            readPoolSize,
            invalid);
    }

    private static string ResolveMemoryLimit(
        string defaultMemory,
        RepoQlConfig.DuckDbSettings settings,
        List<DuckDbEnvironmentIssue> invalid)
    {
        var raw = settings.MemoryLimit;
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

    private static int ResolveThreads(
        string defaultThreads,
        RepoQlConfig.DuckDbSettings settings,
        List<DuckDbEnvironmentIssue> invalid)
    {
        if (!settings.Threads.HasValue)
            return int.Parse(defaultThreads);

        var parsed = settings.Threads.Value;
        if (parsed <= 0)
        {
            var raw = settings.Threads.Value.ToString();
            invalid.Add(new DuckDbEnvironmentIssue("DUCKDB_THREADS", raw, "Thread count must be positive."));
            return int.Parse(defaultThreads);
        }

        return parsed;
    }

    private static string ResolveTempDirectory(string? databasePath, RepoQlConfig.DuckDbSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TempDirectory))
        {
            return settings.TempDirectory.Trim();
        }

        var baseDir = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(".repoql")
            : Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";

        return Path.Combine(baseDir, "temp");
    }

    private static int ResolveReadPoolSize(
        RepoQlConfig.DuckDbSettings settings,
        List<DuckDbEnvironmentIssue> invalid)
    {
        if (!settings.ReadPoolSize.HasValue)
            return DefaultReadPoolSize;

        var parsed = settings.ReadPoolSize.Value;
        if (parsed <= 0 || parsed > MaxReadPoolSize)
        {
            var raw = settings.ReadPoolSize.Value.ToString();
            invalid.Add(new DuckDbEnvironmentIssue(
                "DUCKDB_READ_POOL_SIZE",
                raw,
                $"Read pool size must be between 1 and {MaxReadPoolSize}."));
            return DefaultReadPoolSize;
        }

        return parsed;
    }
}
