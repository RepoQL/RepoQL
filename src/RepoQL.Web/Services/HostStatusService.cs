namespace RepoQL.Web.Services;

/// <summary>
/// Periodically pings the RepoQL host to report availability.
/// </summary>
internal sealed class HostStatusService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
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
                var client = await _connectionManager.GetClientAsync(stoppingToken).ConfigureAwait(false);
                await client.ExecuteRawQueryAsync("SELECT 1", rowLimit: 1, cancellationToken: stoppingToken).ConfigureAwait(false);
                _store.SetSnapshot(HostStatusSnapshot.Online("Host reachable"));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RepoQL host ping failed");
                _store.SetSnapshot(HostStatusSnapshot.Offline(ex.GetBaseException().Message));
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
