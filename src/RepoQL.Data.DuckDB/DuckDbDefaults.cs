namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Centralize DuckDB default configuration calculations.
/// Complexity: Encapsulates hardware-based heuristics so callers share one set of rules.
/// </summary>
internal static class DuckDbDefaults
{
    public static string GetDefaultThreads()
    {
        var logicalCores = Environment.ProcessorCount;
        var estimatedPhysicalCores = logicalCores > 4 ? logicalCores / 2 : logicalCores;
        var threads = estimatedPhysicalCores > 2
            ? Math.Min(estimatedPhysicalCores - 1, 8)
            : estimatedPhysicalCores;

        return Math.Max(1, threads).ToString();
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
        var threads = int.Parse(GetDefaultThreads());
        var memoryStr = GetDefaultMemoryLimit();
        var memoryMb = ParseMemoryMb(memoryStr);

        var memoryPerThread = memoryMb / threads;
        if (memoryPerThread < 1024)
        {
            threads = Math.Max(1, (int)(memoryMb / 1024));
        }

        return (threads.ToString(), $"{memoryMb}MB");
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
