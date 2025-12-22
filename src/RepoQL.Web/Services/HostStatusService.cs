using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Subscribes to the RepoQL host status stream for real-time updates.
/// Falls back to polling on connection failure with automatic reconnection.
/// </summary>
internal sealed class HostStatusService : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly HostStatusStore _store;
    private readonly ILogger<HostStatusService> _logger;

    public HostStatusService(
        RepoQlConnectionManager connectionManager,
        HostStatusStore store,
        ILogger<HostStatusService> logger)
    {
        _connectionManager = connectionManager;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SubscribeToStreamAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Status stream disconnected, will reconnect");
                _store.SetSnapshot(HostStatusSnapshot.Offline($"Disconnected: {ex.GetBaseException().Message}"));

                try
                {
                    await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task SubscribeToStreamAsync(CancellationToken stoppingToken)
    {
        var client = await _connectionManager.GetClientAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogDebug("Subscribing to status stream");
        _store.SetSnapshot(HostStatusSnapshot.Online("Connected, waiting for status..."));

        await foreach (var evt in client.WatchStatusAsync(stoppingToken).ConfigureAwait(false))
        {
            ProcessEvent(evt);
        }
    }

    private void ProcessEvent(StatusEvent evt)
    {
        switch (evt.EventCase)
        {
            case StatusEvent.EventOneofCase.Pipeline:
                ProcessPipelineEvent(evt.Pipeline);
                break;

            case StatusEvent.EventOneofCase.Activity:
                ProcessActivityEvent(evt.Activity);
                break;

            case StatusEvent.EventOneofCase.Health:
                ProcessHealthEvent(evt.Health);
                break;

            case StatusEvent.EventOneofCase.Stats:
                ProcessStatsEvent(evt.Stats);
                break;
        }
    }

    private void ProcessPipelineEvent(PipelineStatusEvent pipeline)
    {
        var status = pipeline.Ready ? "Ready" : GetPipelineStatusText(pipeline);
        _store.SetSnapshot(HostStatusSnapshot.Online(status));
        _store.SetPipelineStatus(pipeline);
    }

    private void ProcessActivityEvent(IndexingActivityEvent activity)
    {
        // Activity events no longer tracked - pipeline status provides all needed info
    }

    private void ProcessHealthEvent(HealthEvent health)
    {
        if (health.Type == HealthEventType.HealthEventDisconnected)
        {
            _store.SetSnapshot(HostStatusSnapshot.Offline(health.Message));
        }
        else
        {
            _store.AddHealthEvent(health);
        }
    }

    private void ProcessStatsEvent(StatsSnapshotEvent stats)
    {
        _store.SetStats(stats);
    }

    private static string GetPipelineStatusText(PipelineStatusEvent pipeline)
    {
        if (pipeline.Reindexing)
            return "Reindexing...";

        var busyStages = pipeline.Stages
            .Where(s => s.Busy || s.Queued > 0 || s.InProgress > 0)
            .Select(s => s.Stage.ToString())
            .ToList();

        if (busyStages.Count == 0)
            return "Idle";

        return $"Processing: {string.Join(", ", busyStages)}";
    }
}
