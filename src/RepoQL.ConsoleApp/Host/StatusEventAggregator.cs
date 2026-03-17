using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Core.Metrics;
using RepoQL.Indexing.Hosting;
using RepoQL.Indexing.Indexing;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Aggregates indexing events and status changes, broadcasting them as <see cref="StatusEvent"/>
/// to any connected subscriber, including the dashboard SSE bridge.
/// </summary>
/// <remarks>
/// <para>Provides event-driven status updates to internal consumers that need a live stream.</para>
/// <para>
/// Subscribes to <see cref="IndexingEngine.StateChanged"/> for fine-grained state transitions
/// and samples <see cref="IIndexingCoordinator.GetPipelineStatus"/> on each change.
/// </para>
/// </remarks>
public sealed class StatusEventAggregator : IDisposable
{
    private readonly IIndexingCoordinator _coordinator;
    private readonly IndexingEngine _engine;
    private readonly StageMetricsListener _stageMetrics;
    private readonly ILogger<StatusEventAggregator> _logger;

    private readonly ConcurrentDictionary<Guid, Channel<StatusEvent>> _subscribers = new();

    private readonly object _stateLock = new();
    private PipelineStatusSnapshot? _lastSnapshot;
    private bool _disposed;

    public StatusEventAggregator(
        IIndexingCoordinator coordinator,
        IndexingEngine engine,
        StageMetricsListener stageMetrics,
        ILogger<StatusEventAggregator>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _stageMetrics = stageMetrics ?? throw new ArgumentNullException(nameof(stageMetrics));
        _logger = logger ?? NullLogger<StatusEventAggregator>.Instance;

        // Subscribe to engine state changes
        _engine.StateChanged += OnStateChanged;
        _engine.HotPathIdle += OnHotPathIdle;

        // Capture initial state
        PublishPipelineStatus();
    }

    /// <summary>
    /// Stream status events to a subscriber. Each subscriber receives all events from subscription time.
    /// </summary>
    public async IAsyncEnumerable<StatusEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriberId = Guid.NewGuid();
        var subscriberChannel = Channel.CreateBounded<StatusEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

        while (!_subscribers.TryAdd(subscriberId, subscriberChannel))
        {
            subscriberId = Guid.NewGuid();
        }

        try
        {
            // Send initial pipeline status immediately on subscription
            var initialStatus = CreatePipelineStatusEvent(_coordinator.GetPipelineStatus());
            yield return initialStatus;

            // Stream all subsequent events
            await foreach (var evt in subscriberChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriberId, out _);
            subscriberChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Publish a custom activity event (e.g., file changed, file parsed).
    /// Called by external code that detects activity.
    /// </summary>
    public void PublishActivity(IndexingActivityType type, string? uri = null, string? message = null, int queuedCount = 0, int processedCount = 0)
    {
        var evt = new StatusEvent
        {
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Activity = new IndexingActivityEvent
            {
                Type = type,
                Uri = uri ?? "",
                Message = message ?? "",
                QueuedCount = queuedCount,
                ProcessedCount = processedCount
            }
        };

        TryPublish(evt);
    }

    /// <summary>
    /// Publish a health event (connected, disconnected, warning, error).
    /// </summary>
    public void PublishHealth(HealthEventType type, string message, HealthSeverity severity = HealthSeverity.Info)
    {
        var evt = new StatusEvent
        {
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Health = new HealthEvent
            {
                Type = type,
                Message = message,
                Severity = severity
            }
        };

        TryPublish(evt);
    }

    /// <summary>
    /// Publish a stats snapshot event.
    /// </summary>
    public void PublishStats(long totalFiles, long totalNodes, int exploreCoveragePercent, bool embeddingsReady)
    {
        var evt = new StatusEvent
        {
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Stats = new StatsSnapshotEvent
            {
                ExploreCoveragePercent = exploreCoveragePercent,
                TotalFiles = totalFiles,
                TotalNodes = totalNodes,
                EmbeddingsReady = embeddingsReady
            }
        };

        TryPublish(evt);
    }

    private void OnStateChanged(object? sender, IndexingStateChangedEventArgs args)
    {
        // State transition - publish updated pipeline status
        PublishPipelineStatus();

        // Derive activity type from state transition
        var activityType = DeriveActivityType(args.OldState, args.NewState);
        if (activityType != IndexingActivityType.IndexingActivityUnspecified)
        {
            var message = GetActivityMessage(activityType, args.NewState);
            PublishActivity(activityType, message: message);
        }
    }

    private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args)
    {
        // Hot path completed a batch
        PublishActivity(IndexingActivityType.IndexingActivityBatchComplete, message: $"Completed epoch {args.Epoch}");
        PublishPipelineStatus();
    }

    private void PublishPipelineStatus()
    {
        var snapshot = _coordinator.GetPipelineStatus();

        lock (_stateLock)
        {
            // Skip if unchanged (except timestamp)
            if (_lastSnapshot != null && SnapshotsEqual(_lastSnapshot, snapshot))
                return;
            _lastSnapshot = snapshot;
        }

        var evt = CreatePipelineStatusEvent(snapshot);
        TryPublish(evt);
    }

    private StatusEvent CreatePipelineStatusEvent(PipelineStatusSnapshot snapshot)
    {
        // Get hot path queue/worker counts from coordinator stages
        var parsingStage = snapshot.Stages.FirstOrDefault(s => s.Stage == CoordinatorPipelineStage.Parsing);
        var hotPathQueued = parsingStage?.Queued ?? 0;
        var hotPathInProgress = parsingStage?.InProgress ?? 0;

        // Get idle processing counts
        var writerStage = snapshot.Stages.FirstOrDefault(s => s.Stage == CoordinatorPipelineStage.Writer);
        var idleActive = writerStage?.InProgress ?? 0;
        var idlePending = writerStage?.Queued ?? 0;

        // Get granular snapshots
        var hotPath = _stageMetrics.GetHotPathSnapshot(hotPathQueued, hotPathInProgress);
        var idleProcessing = _stageMetrics.GetIdleProcessingSnapshot(idleActive, idlePending);

        var evt = new StatusEvent
        {
            Timestamp = Timestamp.FromDateTimeOffset(snapshot.CapturedAt),
            Pipeline = new PipelineStatusEvent
            {
                Reindexing = snapshot.IsReindexing,
                WriterPending = snapshot.WriterPending,
                Ready = IsReady(snapshot),
                HotPath = MapHotPath(hotPath),
                IdleProcessing = MapIdleProcessing(idleProcessing)
            }
        };

        // Legacy stages for backward compatibility
        foreach (var stage in snapshot.Stages)
        {
            var metrics = stage.Stage == CoordinatorPipelineStage.Writer
                ? BuildIdleAggregateMetrics(idleProcessing, stage)
                : _stageMetrics.GetSnapshot(MapStageToMetricName(stage.Stage), stage.Queued, stage.InProgress);

            var status = new StageStatus
            {
                Stage = MapStage(stage.Stage),
                Busy = stage.Busy,
                Queued = (uint)stage.Queued,
                InProgress = (uint)stage.InProgress,
                AvgDurationMs = metrics.AvgDurationMs,
                PeakDurationMs = metrics.PeakDurationMs,
                ProcessedTotal = metrics.ProcessedTotal,
                ThroughputPerSec = metrics.ThroughputPerSec,
                LastRunDurationSec = metrics.LastRunDurationSec,
                LastRunItems = metrics.LastRunItems
            };

            // Add sparkline data
            status.LatencySamples.AddRange(metrics.LatencySamples);
            status.QueueSamples.AddRange(metrics.QueueSamples);

            // Add last active timestamp if available
            if (metrics.LastActive != default)
            {
                status.LastActive = Timestamp.FromDateTimeOffset(metrics.LastActive);
            }

            evt.Pipeline.Stages.Add(status);
        }

        return evt;
    }

    private static StageMetricsListener.StageSnapshot BuildIdleAggregateMetrics(
        StageMetricsListener.IdleProcessingSnapshot idleProcessing,
        PipelineStageStatusSnapshot stage)
    {
        var processedTotal = idleProcessing.Stages.Sum(s => s.ProcessedTotal);
        var peakDurationMs = idleProcessing.Stages.Max(s => s.PeakDurationMs);
        var currentPhase = idleProcessing.Stages.FirstOrDefault(s => s.Busy);
        var throughputPerSec = idleProcessing.Active && idleProcessing.LastRunDurationSec > 0
            ? Math.Max(0, idleProcessing.Progress) / Math.Max(0.1, idleProcessing.LastRunDurationSec)
            : 0;

        return new StageMetricsListener.StageSnapshot
        {
            AvgDurationMs = currentPhase?.AvgDurationMs ?? idleProcessing.AvgDurationMs,
            PeakDurationMs = peakDurationMs,
            ProcessedTotal = processedTotal,
            ThroughputPerSec = throughputPerSec,
            LastActive = idleProcessing.LastRun,
            LastRunDurationSec = idleProcessing.LastRunDurationSec,
            LastRunItems = idleProcessing.LastRunItems,
            LatencySamples = [],
            QueueSamples = [(uint)Math.Max(0, stage.Queued)],
        };
    }

    private static HotPathStatus MapHotPath(StageMetricsListener.HotPathSnapshot snapshot)
    {
        var status = new HotPathStatus
        {
            Active = snapshot.Active,
            Queued = snapshot.Queued,
            InProgress = snapshot.InProgress,
            ThroughputPerSec = snapshot.ThroughputPerSec
        };

        foreach (var stage in snapshot.Stages)
        {
            status.Stages.Add(new StageStatus
            {
                Stage = MapGranularStage(stage.Stage),
                Busy = stage.Busy,
                ProcessedTotal = stage.ProcessedTotal,
                AvgDurationMs = stage.AvgDurationMs,
                PeakDurationMs = stage.PeakDurationMs
            });
        }

        return status;
    }

    private static IdleProcessingStatus MapIdleProcessing(StageMetricsListener.IdleProcessingSnapshot snapshot)
    {
        var status = new IdleProcessingStatus
        {
            Active = snapshot.Active,
            CurrentPhase = snapshot.CurrentPhase ?? "",
            Progress = snapshot.Progress,
            Total = snapshot.Total,
            AvgDurationMs = snapshot.AvgDurationMs,
            LastRunDurationSec = snapshot.LastRunDurationSec,
            LastRunItems = snapshot.LastRunItems
        };

        if (snapshot.LastRun != default)
        {
            status.LastRun = Timestamp.FromDateTimeOffset(snapshot.LastRun);
        }

        foreach (var stage in snapshot.Stages)
        {
            status.Stages.Add(new StageStatus
            {
                Stage = MapGranularStage(stage.Stage),
                Busy = stage.Busy,
                ProcessedTotal = stage.ProcessedTotal,
                AvgDurationMs = stage.AvgDurationMs,
                PeakDurationMs = stage.PeakDurationMs
            });
        }

        return status;
    }

    private static PipelineStage MapGranularStage(string stage) => stage switch
    {
        "discover" => PipelineStage.Discover,
        "filter" => PipelineStage.Filter,
        "classify" => PipelineStage.Classify,
        "parse" => PipelineStage.Parse,
        "explore" => PipelineStage.Explore,
        "commit" => PipelineStage.Commit,
        "prune" => PipelineStage.Prune,
        "structure_embedding" => PipelineStage.StructEmbed,
        "embedding_refresh" => PipelineStage.VectorRefresh,
        "multi_file_analysis" => PipelineStage.MultiFileAnalysis,
        _ => PipelineStage.Unspecified
    };

    /// <summary>
    /// Maps coordinator stage enum to the metric key for StageMetricsListener.
    /// </summary>
    /// <remarks>
    /// Metric keys from repoql.indexing:
    /// - "hotpath": End-to-end file processing (repoql.hotpath.duration)
    /// - "structure_embedding": Structure embedding generation (repoql.idle.phase.duration{phase=structure_embedding})
    /// - "batch": Database commits (repoql.batch.duration)
    /// - "classification", "parsing", etc.: Individual stage timing (repoql.stage.duration)
    /// </remarks>
    private static string MapStageToMetricName(CoordinatorPipelineStage stage) => stage switch
    {
        CoordinatorPipelineStage.Discovery => "classification",       // File discovery uses classification timing
        CoordinatorPipelineStage.Parsing => "hotpath",                // Full indexer pipeline timing
        CoordinatorPipelineStage.Analysis => "single_file_analysis",
        CoordinatorPipelineStage.Writer => "structure_embedding",     // Idle processing = structure embeddings
        _ => "unknown"
    };

    private static bool IsReady(PipelineStatusSnapshot snapshot)
    {
        if (snapshot.IsReindexing || snapshot.WriterPending)
            return false;

        return snapshot.Stages.All(s => !s.Busy && s.Queued == 0 && s.InProgress == 0);
    }

    private static PipelineStage MapStage(CoordinatorPipelineStage stage) => stage switch
    {
        CoordinatorPipelineStage.Discovery => PipelineStage.Discovery,
        CoordinatorPipelineStage.Parsing => PipelineStage.Indexing,
        CoordinatorPipelineStage.Analysis => PipelineStage.Analysis,
        CoordinatorPipelineStage.Writer => PipelineStage.SemanticIndexing,
        _ => PipelineStage.Unspecified
    };

    private static bool SnapshotsEqual(PipelineStatusSnapshot a, PipelineStatusSnapshot b)
    {
        if (a.IsReindexing != b.IsReindexing) return false;
        if (a.WriterPending != b.WriterPending) return false;
        if (a.Stages.Count != b.Stages.Count) return false;

        for (int i = 0; i < a.Stages.Count; i++)
        {
            var sa = a.Stages[i];
            var sb = b.Stages[i];
            if (sa.Stage != sb.Stage || sa.Busy != sb.Busy || sa.Queued != sb.Queued || sa.InProgress != sb.InProgress)
                return false;
        }

        return true;
    }

    private static IndexingActivityType DeriveActivityType(IndexingState oldState, IndexingState newState)
    {
        // Detect transitions from busy to idle
        if (oldState.HasFlag(IndexingState.ClassificationBusy) && !newState.HasFlag(IndexingState.ClassificationBusy))
            return IndexingActivityType.IndexingActivityFileDiscovered;

        if (oldState.HasFlag(IndexingState.ParsingBusy) && !newState.HasFlag(IndexingState.ParsingBusy))
            return IndexingActivityType.IndexingActivityFileParsed;

        if (oldState.HasFlag(IndexingState.SingleFileAnalysisBusy) && !newState.HasFlag(IndexingState.SingleFileAnalysisBusy))
            return IndexingActivityType.IndexingActivityFileAnalyzed;

        if (oldState.HasFlag(IndexingState.MultiFileAnalysisBusy) && !newState.HasFlag(IndexingState.MultiFileAnalysisBusy))
            return IndexingActivityType.IndexingActivityFileAnalyzed;

        // Detect transition to all idle
        if (!oldState.HasFlag(IndexingState.AllIdle) && newState.HasFlag(IndexingState.AllIdle))
            return IndexingActivityType.IndexingActivityIdle;

        return IndexingActivityType.IndexingActivityUnspecified;
    }

    private static string GetActivityMessage(IndexingActivityType type, IndexingState state)
    {
        return type switch
        {
            IndexingActivityType.IndexingActivityFileDiscovered => "Classification complete",
            IndexingActivityType.IndexingActivityFileParsed => "Parsing complete",
            IndexingActivityType.IndexingActivityFileAnalyzed => "Analysis complete",
            IndexingActivityType.IndexingActivityIdle => "Pipeline idle",
            _ => ""
        };
    }

    private void TryPublish(StatusEvent evt)
    {
        if (_disposed)
            return;

        foreach (var subscriber in _subscribers.Values)
        {
            if (!subscriber.Writer.TryWrite(evt))
            {
                _logger.LogDebug("Status event dropped (channel full)");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _engine.StateChanged -= OnStateChanged;
        _engine.HotPathIdle -= OnHotPathIdle;

        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }

        _subscribers.Clear();
    }
}
