using System.Linq;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Indexing.Hosting;

namespace RepoQL.ConsoleApp.Host;

internal sealed class PipelineHealthPublisher(
    IIndexingCoordinator coordinator,
    HealthServiceImpl health,
    ILogger<PipelineHealthPublisher>? logger = null) : BackgroundService
{
    private readonly ILogger<PipelineHealthPublisher> _logger = logger ?? NullLogger<PipelineHealthPublisher>.Instance;
    private readonly Dictionary<string, HealthCheckResponse.Types.ServingStatus> _lastStatus = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            PublishSnapshot(coordinator.GetPipelineStatus());
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                PublishSnapshot(coordinator.GetPipelineStatus());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline health publisher failed");
        }
    }

    private void PublishSnapshot(PipelineStatusSnapshot snapshot)
    {
        var byStage = snapshot.Stages.ToDictionary(s => s.Stage, s => s);
        SetStatus("repoql.discovery", IsStageIdle(byStage, CoordinatorPipelineStage.Discovery));
        SetStatus("repoql.parsing", IsStageIdle(byStage, CoordinatorPipelineStage.Parsing));
        SetStatus("repoql.analysis", IsStageIdle(byStage, CoordinatorPipelineStage.Analysis));
        SetStatus("repoql.writer", IsStageIdle(byStage, CoordinatorPipelineStage.Writer));
        SetStatus("repoql.reindex", !snapshot.IsReindexing);
        var allIdle = byStage.Values.All(IsIdleStatus) && !snapshot.IsReindexing && !snapshot.WriterPending;
        SetStatus("repoql.ready", allIdle);
    }

    private static bool IsStageIdle(IReadOnlyDictionary<CoordinatorPipelineStage, PipelineStageStatusSnapshot> lookup, CoordinatorPipelineStage stage)
        => !lookup.TryGetValue(stage, out var status) || IsIdleStatus(status);

    private static bool IsIdleStatus(PipelineStageStatusSnapshot status)
        => status is
        {
            Busy: false, 
            Queued: 0, 
            InProgress: 0
        };

    private void SetStatus(string service, bool serving)
    {
        var status = serving ? HealthCheckResponse.Types.ServingStatus.Serving : HealthCheckResponse.Types.ServingStatus.NotServing;
        if (_lastStatus.TryGetValue(service, out var current) && current == status)
            return;
        _lastStatus[service] = status;
        health.SetStatus(service, status);
    }
}
