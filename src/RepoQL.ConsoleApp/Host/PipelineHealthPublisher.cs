using System;
using System.Collections.Generic;
using System.Threading;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Core;

namespace RepoQL.ConsoleApp.Host;

internal sealed class PipelineHealthPublisher(
    IRepositoryIndexer indexer,
    HealthServiceImpl health,
    ILogger<PipelineHealthPublisher>? logger = null) : BackgroundService
{
    private readonly IRepositoryIndexer _indexer = indexer;
    private readonly HealthServiceImpl _health = health;
    private readonly ILogger<PipelineHealthPublisher> _logger = logger ?? NullLogger<PipelineHealthPublisher>.Instance;
    private readonly Dictionary<string, HealthCheckResponse.Types.ServingStatus> _lastStatus = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            PublishSnapshot(_indexer.GetPipelineSnapshot());
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                PublishSnapshot(_indexer.GetPipelineSnapshot());
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

    private void PublishSnapshot(PipelineSnapshot snapshot)
    {
        SetStatus("repoql.discovery", snapshot.Discovery.IsIdle);
        SetStatus("repoql.parsing", snapshot.Parsing.IsIdle);
        SetStatus("repoql.analysis", snapshot.Analysis.IsIdle);
        SetStatus("repoql.reindex", !snapshot.IsReindexing);
        SetStatus("repoql.ready", snapshot.Ready);
    }

    private void SetStatus(string service, bool serving)
    {
        var status = serving ? HealthCheckResponse.Types.ServingStatus.Serving : HealthCheckResponse.Types.ServingStatus.NotServing;
        if (_lastStatus.TryGetValue(service, out var current) && current == status)
            return;
        _lastStatus[service] = status;
        _health.SetStatus(service, status);
    }
}
