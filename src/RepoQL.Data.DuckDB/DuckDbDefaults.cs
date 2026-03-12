namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Centralize DuckDB default configuration calculations.
/// Complexity: Encapsulates hardware-based heuristics so callers share one set of rules.
/// </summary>
internal static class DuckDbDefaults
{
    public static string GetDefaultThreads()
    {
        // Single thread: RepoQL's tables are small enough (~50K rows) that intra-query
        // parallelism adds no measurable benefit. 1 thread minimizes native buffer overhead
        // and leaves maximum memory headroom for the .NET managed heap.
        // Concurrent read queries from multiple gRPC clients still work at the connection level.
        // Overridable via DUCKDB_THREADS setting.
        return "1";
    }

    public static string GetDefaultMemoryLimit()
    {
        try
        {
            var totalMemoryMb = GetTotalAvailableMemoryMb();
            var targetMb = Math.Min(16384, (long)(totalMemoryMb * 0.6));
            targetMb = Math.Max(512, targetMb);
            return $"{targetMb}MB";
        }
        catch
        {
            return "4GB";
        }
    }

    public static long GetTotalAvailableMemoryMb()
    {
        var gcMemoryInfo = GC.GetGCMemoryInfo();
        return gcMemoryInfo.TotalAvailableMemoryBytes / (1024 * 1024);
    }

    public static (string threads, string memory) GetOptimalConfig()
    {
        return (GetDefaultThreads(), GetDefaultMemoryLimit());
    }

    public static long ParseMemoryMb(string memoryStr)
    {
        var normalized = memoryStr.Trim().ToUpperInvariant();

        if (normalized.EndsWith("GB", StringComparison.Ordinal))
        {
            return long.Parse(normalized[..^2]) * 1024;
        }
        if (normalized.EndsWith("MB", StringComparison.Ordinal))
        {
            return long.Parse(normalized[..^2]);
        }

        return long.Parse(normalized) / (1024 * 1024);
    }
}
