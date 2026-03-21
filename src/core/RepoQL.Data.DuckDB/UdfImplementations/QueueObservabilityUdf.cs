using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Diagnostics;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for queue and runtime observability.
///
/// Purpose: Expose queue activity and host health as SQL tables for diagnostics.
///
/// Complexity: Reads in-memory diagnostics and registry snapshots, then augments
/// with process and filesystem metrics using safe fallbacks on error.
/// </summary>
[UdfClass]
public sealed class QueueObservabilityUdf
{
    private readonly IIndexingDiagnosticsProvider? _diagnosticsProvider;
    private readonly UriRegistry? _registry;
    private readonly ILogger<QueueObservabilityUdf> _logger;
    private readonly string? _repoRoot;

    public QueueObservabilityUdf(
        IIndexingDiagnosticsProvider? diagnosticsProvider = null,
        UriRegistry? registry = null,
        RepositoryConfiguration? repoConfig = null,
        ILogger<QueueObservabilityUdf>? logger = null)
    {
        _diagnosticsProvider = diagnosticsProvider;
        _registry = registry;
        _repoRoot = repoConfig?.Path;
        _logger = logger ?? NullLogger<QueueObservabilityUdf>.Instance;
    }

    /// <summary>
    /// Returns the current queue snapshot (queued + processing items).
    /// </summary>
    [StructuredUdf("_processing_queue_internal", MacroName = "processing_queue",
        Description = "Returns queued and in-flight indexing items")]
    public IEnumerable<ProcessingQueueRow> ProcessingQueue([UdfDefault("''")] string? _dummy)
    {
        if (_diagnosticsProvider is null)
            return [];

        IReadOnlyList<QueuedItemInfo> items;
        try
        {
            items = _diagnosticsProvider.GetQueuedItems();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "processing_queue failed to get queued items.");
            return [];
        }

        return BuildQueueRows(items);
    }

    private static IEnumerable<ProcessingQueueRow> BuildQueueRows(IReadOnlyList<QueuedItemInfo> items)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var elapsedSeconds = (now - item.EnqueuedAt).TotalSeconds;
            var ageSeconds = elapsedSeconds <= 0
                ? 0
                : elapsedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)elapsedSeconds;

            yield return new ProcessingQueueRow(
                item.Uri,
                item.Stage,
                item.Status,
                ageSeconds,
                item.Size,
                item.MimeType);
        }
    }

    /// <summary>
    /// Returns a single-row summary of host and indexing health.
    /// </summary>
    [StructuredUdf("_system_health_internal", MacroName = "system_health",
        Description = "Returns single-row system health summary")]
    public IEnumerable<SystemHealthRow> SystemHealth([UdfDefault("''")] string? _dummy)
    {
        if (_diagnosticsProvider is null || _registry is null)
            return [CreateErrorSystemHealthRow()];

        try
        {
            var snapshot = _diagnosticsProvider.GetSnapshot();
            var summary = _registry.GetSummary();

            var queueDepth = snapshot.HotPathDepth
                             + snapshot.AnalysisDepth
                             + snapshot.IdlePending
                             + snapshot.IdleActive
                             + snapshot.WriterPending;

            var activeWorkers = snapshot.HotPathActive
                                + snapshot.AnalysisActive
                                + snapshot.IdleActive;

            var hostMemoryMb = (int)Math.Clamp(
                Process.GetCurrentProcess().WorkingSet64 / (1024L * 1024L),
                0L,
                int.MaxValue);

            return
            [
                new SystemHealthRow(
                    snapshot.Status,
                    queueDepth,
                    activeWorkers,
                    summary.IndexFailed,
                    summary.IndexStale,
                    snapshot.Epoch,
                    snapshot.LastError,
                    hostMemoryMb,
                    GetDatabaseSizeMb(),
                    GetDiskFreeMb())
            ];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "system_health failed to build health snapshot.");
            return [CreateErrorSystemHealthRow()];
        }
    }

    private SystemHealthRow CreateErrorSystemHealthRow()
        => new(
            Status: "error",
            QueueDepth: 0,
            ActiveWorkers: 0,
            FailedCount: 0,
            StaleCount: 0,
            Epoch: 0,
            LastError: null,
            HostMemoryMb: 0,
            DbSizeMb: 0,
            DiskFreeMb: 0);

    private int GetDatabaseSizeMb()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_repoRoot))
                return 0;

            var dbPath = Path.Combine(_repoRoot, ".repoql", "index.duckdb");
            var file = new FileInfo(dbPath);
            if (!file.Exists)
                return 0;

            return (int)Math.Clamp(file.Length / (1024L * 1024L), 0L, int.MaxValue);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read database size.");
            return 0;
        }
    }

    private int GetDiskFreeMb()
    {
        try
        {
            var basePath = !string.IsNullOrWhiteSpace(_repoRoot)
                ? _repoRoot
                : Environment.CurrentDirectory;

            var rootPath = Path.GetPathRoot(Path.GetFullPath(basePath));
            if (string.IsNullOrWhiteSpace(rootPath))
                return 0;

            var driveInfo = new DriveInfo(rootPath);
            return (int)Math.Clamp(driveInfo.AvailableFreeSpace / (1024L * 1024L), 0L, int.MaxValue);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read disk free space.");
            return 0;
        }
    }

    public record ProcessingQueueRow(
        string Uri,
        string Stage,
        string Status,
        int AgeSeconds,
        long SizeBytes,
        string? MimeType);

    public record SystemHealthRow(
        string Status,
        int QueueDepth,
        int ActiveWorkers,
        int FailedCount,
        int StaleCount,
        long Epoch,
        string? LastError,
        int HostMemoryMb,
        int DbSizeMb,
        int DiskFreeMb);
}
