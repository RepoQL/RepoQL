using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace RepoQL.Core.Metrics;

/// <summary>
/// Listens to the StageDuration histogram metric and tracks per-stage metrics
/// including rolling averages, sparkline data, and phase-level statistics.
/// </summary>
/// <remarks>
/// Uses the same MeterListener pattern as <see cref="InMemoryMetricsSink"/> but
/// focuses specifically on stage timing data with rich aggregation for UI display.
/// </remarks>
public sealed class StageMetricsListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, StageMetricsState> _stages = new(StringComparer.Ordinal);
    private const int SampleCount = 20;  // Buffer size for sparklines

    public StageMetricsListener()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != "RepoQL.Indexing") return;

            // Subscribe to relevant histograms
            switch (instrument.Name)
            {
                case "repoql.stage.duration":       // Per-stage timing (classification, parsing, etc.)
                case "repoql.hotpath.duration":     // End-to-end hot path
                case "repoql.embed.duration":       // Embedding timing
                case "repoql.batch.duration":       // Batch commit timing
                case "repoql.idle.phase.duration":  // Idle phase timing (structure_embedding, etc.)
                    listener.EnableMeasurementEvents(instrument);
                    break;
            }
        };

        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
    }

    private void OnMeasurement(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        // Determine the key based on instrument type
        string? key = instrument.Name switch
        {
            "repoql.stage.duration" => ExtractTag(tags, "stage"),   // classification, parsing, etc.
            "repoql.hotpath.duration" => "hotpath",                  // End-to-end timing
            "repoql.embed.duration" => "embedding",                  // Embedding timing
            "repoql.batch.duration" => "batch",                      // Batch commit timing
            "repoql.idle.phase.duration" => ExtractTag(tags, "phase"), // structure_embedding, vector_refresh, etc.
            _ => null
        };

        if (key is null) return;

        _stages.AddOrUpdate(
            key,
            _ => new StageMetricsState(SampleCount).Record(value),
            (_, existing) => existing.Record(value));
    }

    private static string? ExtractTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
                return tag.Value?.ToString();
        }
        return null;
    }

    /// <summary>
    /// Gets a snapshot of metrics for the specified stage, including current queue/active state.
    /// </summary>
    public StageSnapshot GetSnapshot(string stageName, int currentQueued, int currentInProgress)
    {
        if (!_stages.TryGetValue(stageName, out var state))
            return new StageSnapshot();

        return state.ToSnapshot(currentQueued, currentInProgress);
    }

    /// <summary>
    /// Gets snapshots for all known stages.
    /// </summary>
    public IReadOnlyDictionary<string, StageSnapshot> GetAllSnapshots()
    {
        var result = new Dictionary<string, StageSnapshot>();
        foreach (var (name, state) in _stages)
        {
            result[name] = state.ToSnapshot(0, 0);
        }
        return result;
    }

    /// <summary>
    /// Gets a hot path status snapshot combining metrics from all hot path stages.
    /// </summary>
    /// <param name="queued">Current queue depth from coordinator.</param>
    /// <param name="inProgress">Current in-progress count from coordinator.</param>
    public HotPathSnapshot GetHotPathSnapshot(int queued, int inProgress)
    {
        // Hot path stages
        var classify = GetSnapshot("classification", 0, 0);
        var parse = GetSnapshot("parsing", 0, 0);
        var explore = GetSnapshot("single_file_analysis", 0, 0);
        var commit = GetSnapshot("batch", 0, 0);
        var hotpath = GetSnapshot("hotpath", queued, inProgress);

        var active = queued > 0 || inProgress > 0;
        var throughput = hotpath.ThroughputPerSec;

        // If hotpath metric isn't active, try to calculate from parsing
        if (throughput == 0 && parse.ThroughputPerSec > 0)
            throughput = parse.ThroughputPerSec;

        return new HotPathSnapshot
        {
            Active = active,
            Queued = queued,
            InProgress = inProgress,
            ThroughputPerSec = throughput,
            Stages =
            [
                new StageMetrics { Stage = "discover", ProcessedTotal = 0 },  // No timing for discover
                new StageMetrics { Stage = "filter", ProcessedTotal = 0 },    // No timing for filter
                new StageMetrics { Stage = "classify", AvgDurationMs = classify.AvgDurationMs, PeakDurationMs = classify.PeakDurationMs, ProcessedTotal = classify.ProcessedTotal, Busy = classify.ThroughputPerSec > 0 },
                new StageMetrics { Stage = "parse", AvgDurationMs = parse.AvgDurationMs, PeakDurationMs = parse.PeakDurationMs, ProcessedTotal = parse.ProcessedTotal, Busy = parse.ThroughputPerSec > 0 },
                new StageMetrics { Stage = "explore", AvgDurationMs = explore.AvgDurationMs, PeakDurationMs = explore.PeakDurationMs, ProcessedTotal = explore.ProcessedTotal, Busy = explore.ThroughputPerSec > 0 },
                new StageMetrics { Stage = "commit", AvgDurationMs = commit.AvgDurationMs, PeakDurationMs = commit.PeakDurationMs, ProcessedTotal = commit.ProcessedTotal, Busy = commit.ThroughputPerSec > 0 }
            ]
        };
    }

    /// <summary>
    /// Gets an idle processing status snapshot.
    /// </summary>
    /// <param name="activeIdleProcessing">Count of active idle processing workers.</param>
    /// <param name="pendingIdleProcessing">Count of pending idle processing items.</param>
    public IdleProcessingSnapshot GetIdleProcessingSnapshot(int activeIdleProcessing, int pendingIdleProcessing)
    {
        var prune = GetSnapshot("prune", 0, 0);
        var structEmbed = GetSnapshot("structure_embedding", 0, 0);
        var vectorRefresh = GetSnapshot("vector_refresh", 0, 0);
        var analysis = GetSnapshot("multi_file_analysis", 0, 0);

        var active = activeIdleProcessing > 0 || pendingIdleProcessing > 0;

        // Determine current phase based on which has recent activity
        string? currentPhase = null;
        var now = DateTimeOffset.UtcNow;
        var recentThreshold = TimeSpan.FromSeconds(5);

        if (prune.LastActive != default && now - prune.LastActive < recentThreshold)
            currentPhase = "prune";
        else if (structEmbed.LastActive != default && now - structEmbed.LastActive < recentThreshold)
            currentPhase = "structure_embedding";
        else if (vectorRefresh.LastActive != default && now - vectorRefresh.LastActive < recentThreshold)
            currentPhase = "vector_refresh";
        else if (analysis.LastActive != default && now - analysis.LastActive < recentThreshold)
            currentPhase = "multi_file_analysis";

        // Find the most recent last run
        var allPhases = new[] { prune, structEmbed, vectorRefresh, analysis };
        var lastRun = allPhases.Where(p => p.LastActive != default).MaxBy(p => p.LastActive)?.LastActive ?? default;
        var lastRunDuration = allPhases.Where(p => p.LastRunDurationSec > 0).Sum(p => p.LastRunDurationSec);
        var lastRunItems = allPhases.Sum(p => p.LastRunItems);

        return new IdleProcessingSnapshot
        {
            Active = active,
            CurrentPhase = currentPhase,
            Progress = activeIdleProcessing,
            Total = activeIdleProcessing + pendingIdleProcessing,
            AvgDurationMs = structEmbed.AvgDurationMs,  // Use structure embedding as representative
            LastRun = lastRun,
            LastRunDurationSec = lastRunDuration,
            LastRunItems = lastRunItems,
            Stages =
            [
                new StageMetrics { Stage = "prune", AvgDurationMs = prune.AvgDurationMs, PeakDurationMs = prune.PeakDurationMs, ProcessedTotal = prune.ProcessedTotal, Busy = currentPhase == "prune" },
                new StageMetrics { Stage = "structure_embedding", AvgDurationMs = structEmbed.AvgDurationMs, PeakDurationMs = structEmbed.PeakDurationMs, ProcessedTotal = structEmbed.ProcessedTotal, Busy = currentPhase == "structure_embedding" },
                new StageMetrics { Stage = "vector_refresh", AvgDurationMs = vectorRefresh.AvgDurationMs, PeakDurationMs = vectorRefresh.PeakDurationMs, ProcessedTotal = vectorRefresh.ProcessedTotal, Busy = currentPhase == "vector_refresh" },
                new StageMetrics { Stage = "multi_file_analysis", AvgDurationMs = analysis.AvgDurationMs, PeakDurationMs = analysis.PeakDurationMs, ProcessedTotal = analysis.ProcessedTotal, Busy = currentPhase == "multi_file_analysis" }
            ]
        };
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Internal mutable state tracking metrics for a single pipeline stage.
    /// Thread-safe via lock.
    /// </summary>
    private sealed class StageMetricsState
    {
        private readonly object _lock = new();
        private readonly int _sampleCount;

        // EMA and cumulative stats
        private double _avgDurationMs;
        private double _peakDurationMs;
        private long _processedTotal;

        // Sparkline buffers with adaptive sampling
        private readonly Queue<double> _latencySamples;
        private readonly Queue<uint> _queueSamples;
        private DateTimeOffset _lastLatencySample;
        private DateTimeOffset _lastQueueSample;

        // Phase tracking
        private DateTimeOffset _runStart;
        private DateTimeOffset _lastActive;
        private long _runItems;
        private double _lastRunDuration;
        private long _lastRunItems;
        private bool _wasActive;

        public StageMetricsState(int sampleCount)
        {
            _sampleCount = sampleCount;
            _latencySamples = new Queue<double>(sampleCount);
            _queueSamples = new Queue<uint>(sampleCount);
        }

        public StageMetricsState Record(double durationMs)
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;

                // Track run start (first item after idle) - clear buffers for fresh trend
                if (!_wasActive)
                {
                    _runStart = now;
                    _wasActive = true;
                    _peakDurationMs = durationMs;
                    _runItems = 1;
                    // Clear sparklines to show fresh trend from start
                    _latencySamples.Clear();
                    _queueSamples.Clear();
                    _lastLatencySample = default;
                    _lastQueueSample = default;
                }
                else
                {
                    _runItems++;
                }

                // Update EMA (alpha = 0.2 for responsive but smooth average)
                _avgDurationMs = _avgDurationMs == 0
                    ? durationMs
                    : _avgDurationMs * 0.8 + durationMs * 0.2;

                // Track peak for current run
                _peakDurationMs = Math.Max(_peakDurationMs, durationMs);
                _processedTotal++;

                // Add latency sample with adaptive interval based on run duration
                var runDuration = (now - _runStart).TotalSeconds;
                var adaptiveInterval = GetAdaptiveInterval(runDuration);
                if (now - _lastLatencySample >= adaptiveInterval)
                {
                    if (_latencySamples.Count >= _sampleCount)
                        _latencySamples.Dequeue();
                    _latencySamples.Enqueue(durationMs);
                    _lastLatencySample = now;
                }

                _lastActive = now;
            }
            return this;
        }

        private void UpdateQueue(uint queued)
        {
            var now = DateTimeOffset.UtcNow;

            // Adaptive interval based on run duration
            var runDuration = _wasActive ? (now - _runStart).TotalSeconds : 0;
            var adaptiveInterval = GetAdaptiveInterval(runDuration);

            if (now - _lastQueueSample >= adaptiveInterval)
            {
                if (_queueSamples.Count >= _sampleCount)
                    _queueSamples.Dequeue();
                _queueSamples.Enqueue(queued);
                _lastQueueSample = now;
            }

            // Detect run completion (queue emptied after activity)
            if (_wasActive && queued == 0)
            {
                _lastRunDuration = (now - _runStart).TotalSeconds;
                _lastRunItems = _runItems;
                _wasActive = false;
            }
        }

        /// <summary>
        /// Returns adaptive sample interval based on run duration.
        /// Short runs sample frequently, long runs sample less often to show full trend.
        /// </summary>
        private static TimeSpan GetAdaptiveInterval(double runDurationSeconds)
        {
            // Target: 20 samples across the run
            // Min 200ms, max 5s between samples
            var targetInterval = Math.Max(0.2, runDurationSeconds / 15);
            targetInterval = Math.Min(targetInterval, 5.0);
            return TimeSpan.FromSeconds(targetInterval);
        }

        public StageSnapshot ToSnapshot(int currentQueued, int currentInProgress)
        {
            lock (_lock)
            {
                UpdateQueue((uint)currentQueued);

                // Calculate throughput for active runs
                var runElapsed = (DateTimeOffset.UtcNow - _runStart).TotalSeconds;
                var throughput = _wasActive && runElapsed > 0.1
                    ? _runItems / runElapsed
                    : 0;

                return new StageSnapshot
                {
                    AvgDurationMs = _avgDurationMs,
                    PeakDurationMs = _peakDurationMs,
                    ProcessedTotal = _processedTotal,
                    ThroughputPerSec = throughput,
                    LatencySamples = _latencySamples.ToArray(),
                    QueueSamples = _queueSamples.ToArray(),
                    LastActive = _lastActive,
                    LastRunDurationSec = _lastRunDuration,
                    LastRunItems = _lastRunItems
                };
            }
        }
    }

    /// <summary>
    /// Immutable snapshot of stage metrics for display.
    /// </summary>
    public sealed record StageSnapshot
    {
        public double AvgDurationMs { get; init; }
        public double PeakDurationMs { get; init; }
        public long ProcessedTotal { get; init; }
        public double ThroughputPerSec { get; init; }
        public double[] LatencySamples { get; init; } = [];
        public uint[] QueueSamples { get; init; } = [];
        public DateTimeOffset LastActive { get; init; }
        public double LastRunDurationSec { get; init; }
        public long LastRunItems { get; init; }
    }

    /// <summary>
    /// Snapshot of hot path status for UI display.
    /// </summary>
    public sealed record HotPathSnapshot
    {
        public bool Active { get; init; }
        public int Queued { get; init; }
        public int InProgress { get; init; }
        public double ThroughputPerSec { get; init; }
        public IReadOnlyList<StageMetrics> Stages { get; init; } = [];
    }

    /// <summary>
    /// Snapshot of idle processing status for UI display.
    /// </summary>
    public sealed record IdleProcessingSnapshot
    {
        public bool Active { get; init; }
        public string? CurrentPhase { get; init; }
        public int Progress { get; init; }
        public int Total { get; init; }
        public double AvgDurationMs { get; init; }
        public DateTimeOffset LastRun { get; init; }
        public double LastRunDurationSec { get; init; }
        public long LastRunItems { get; init; }
        public IReadOnlyList<StageMetrics> Stages { get; init; } = [];
    }

    /// <summary>
    /// Metrics for a single pipeline stage.
    /// </summary>
    public sealed record StageMetrics
    {
        public string Stage { get; init; } = "";
        public bool Busy { get; init; }
        public long ProcessedTotal { get; init; }
        public double AvgDurationMs { get; init; }
        public double PeakDurationMs { get; init; }
        public long FilteredCount { get; init; }
    }
}
