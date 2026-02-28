using System.Diagnostics;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Expose host process memory stats as SQL-callable functions.
/// Complexity: Thin wrappers around .NET GC and Process APIs. Pure runtime reads.
/// </summary>
[UdfClass]
public class HostMemoryUdf
{
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
}
