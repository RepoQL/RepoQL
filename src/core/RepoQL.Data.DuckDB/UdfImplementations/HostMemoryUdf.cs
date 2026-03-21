using System.Diagnostics;
using System.Text.Json;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Expose host process memory stats as SQL-callable functions.
/// Complexity: Thin wrappers around .NET GC and Process APIs. Pure runtime reads.
/// </summary>
[UdfClass]
public class HostMemoryUdf
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Returns the host process working set in bytes.
    /// </summary>
    [ScalarUdf("_host_working_set_internal", MacroName = "host_working_set",
        Description = "Returns host process working set in bytes", IsPure = false)]
    public long WorkingSet([UdfDefault("''")] string? _dummy) =>
        Environment.WorkingSet;

    /// <summary>
    /// Returns the .NET managed heap size in bytes (approximate).
    /// </summary>
    [ScalarUdf("_host_managed_heap_internal", MacroName = "host_managed_heap",
        Description = "Returns .NET managed heap size in bytes", IsPure = false)]
    public long ManagedHeap([UdfDefault("''")] string? _dummy) =>
        GC.GetTotalMemory(forceFullCollection: false);

    /// <summary>
    /// Returns total available system memory in bytes.
    /// </summary>
    [ScalarUdf("_host_total_memory_internal", MacroName = "host_total_memory",
        Description = "Returns total available system memory in bytes", IsPure = false)]
    public long TotalMemory([UdfDefault("''")] string? _dummy) =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>
    /// Returns GC collection counts as "gen0:N gen1:N gen2:N".
    /// </summary>
    [ScalarUdf("_host_gc_counts_internal", MacroName = "host_gc_counts",
        Description = "Returns GC generation collection counts", IsPure = false)]
    public string GcCounts([UdfDefault("''")] string? _dummy) =>
        $"gen0:{GC.CollectionCount(0)} gen1:{GC.CollectionCount(1)} gen2:{GC.CollectionCount(2)}";

    /// <summary>
    /// Returns detailed host process memory metrics as JSON.
    /// </summary>
    [ScalarUdf("_host_process_memory_internal", MacroName = "host_process_memory",
        Description = "Returns host process memory details as JSON", IsPure = false)]
    public string ProcessMemory([UdfDefault("''")] string? _dummy)
    {
        using var process = Process.GetCurrentProcess();

        var snapshot = new HostProcessMemorySnapshot(
            WorkingSetBytes: SafeRead(() => process.WorkingSet64),
            PeakWorkingSetBytes: SafeRead(() => process.PeakWorkingSet64),
            PrivateMemoryBytes: SafeRead(() => process.PrivateMemorySize64),
            PagedMemoryBytes: SafeRead(() => process.PagedMemorySize64),
            VirtualMemoryBytes: SafeRead(() => process.VirtualMemorySize64));

        return JsonSerializer.Serialize(snapshot, s_jsonOptions);
    }

    /// <summary>
    /// Returns detailed GC memory metrics as JSON.
    /// </summary>
    [ScalarUdf("_host_gc_memory_info_internal", MacroName = "host_gc_memory_info",
        Description = "Returns detailed GC memory information as JSON", IsPure = false)]
    public string GcMemoryInfo([UdfDefault("''")] string? _dummy)
    {
        var info = GC.GetGCMemoryInfo();
        var snapshot = new HostGcMemorySnapshot(
            HeapSizeBytes: SafeNonNegative(info.HeapSizeBytes),
            FragmentedBytes: SafeNonNegative(info.FragmentedBytes),
            CommittedBytes: SafeNonNegative(info.TotalCommittedBytes),
            MemoryLoadBytes: SafeNonNegative(info.MemoryLoadBytes),
            HighMemoryLoadThresholdBytes: SafeNonNegative(info.HighMemoryLoadThresholdBytes),
            TotalAvailableMemoryBytes: SafeNonNegative(info.TotalAvailableMemoryBytes),
            FinalizationPendingCount: (int)Math.Clamp(info.FinalizationPendingCount, 0, int.MaxValue),
            PauseTimePercentage: Math.Max(0, info.PauseTimePercentage));

        return JsonSerializer.Serialize(snapshot, s_jsonOptions);
    }

    private static long SafeRead(Func<long> getter)
    {
        try
        {
            return SafeNonNegative(getter());
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeNonNegative(long value) => value < 0 ? 0 : value;

    private sealed record HostProcessMemorySnapshot(
        long WorkingSetBytes,
        long PeakWorkingSetBytes,
        long PrivateMemoryBytes,
        long PagedMemoryBytes,
        long VirtualMemoryBytes);

    private sealed record HostGcMemorySnapshot(
        long HeapSizeBytes,
        long FragmentedBytes,
        long CommittedBytes,
        long MemoryLoadBytes,
        long HighMemoryLoadThresholdBytes,
        long TotalAvailableMemoryBytes,
        int FinalizationPendingCount,
        double PauseTimePercentage);
}
