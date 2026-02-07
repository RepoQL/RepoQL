namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Poll Aspire for resource state and expose the latest host snapshot.
/// Complexity: Periodic background polling with connection-change logging and safe snapshots.
/// </summary>
internal sealed class AspireHostStateMonitor : IHostStateProvider, IAsyncDisposable
{
    private readonly AspireMcpClient _client;
    private readonly TimeSpan _pollInterval;
    private readonly object _sync = new();
    private HostStateSnapshot _snapshot;
    private bool _disposed;
    private bool _lastConnected;
    private bool _hasObservedState;
    private Task? _pollingTask;

    public AspireHostStateMonitor(AspireMcpClient client, TimeSpan pollInterval)
    {
        _client = client;
        _pollInterval = pollInterval;
        _snapshot = new HostStateSnapshot(HostState.Unknown, false, null, DateTimeOffset.UtcNow);
    }

    public HostStateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (_pollingTask is not null)
            return;

        _pollingTask = Task.Run(() => PollAsync(cancellationToken), CancellationToken.None);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        HostStateSnapshot snapshot;

        try
        {
            var resources = await _client.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
            snapshot = BuildSnapshot(resources, aspireConnected: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snapshot = new HostStateSnapshot(HostState.Unknown, false, null, DateTimeOffset.UtcNow);
            LogConnectionChange(snapshot.AspireConnected, ex.Message);
            UpdateSnapshot(snapshot);
            return;
        }

        LogConnectionChange(snapshot.AspireConnected, null);
        UpdateSnapshot(snapshot);
    }

    private HostStateSnapshot BuildSnapshot(IReadOnlyList<AspireResource> resources, bool aspireConnected)
    {
        string? hostName = null;
        string? hostState = null;

        foreach (var resource in resources)
        {
            if (!string.Equals(resource.Name, "host", StringComparison.OrdinalIgnoreCase))
                continue;

            hostName = resource.Name;
            hostState = resource.State;
            break;
        }

        if (string.IsNullOrWhiteSpace(hostName))
            return new HostStateSnapshot(HostState.Unknown, aspireConnected, null, DateTimeOffset.UtcNow);

        var mappedState = AspireResourceStateMapper.MapToHostState(hostState);
        return new HostStateSnapshot(mappedState, aspireConnected, hostName, DateTimeOffset.UtcNow);
    }

    private void UpdateSnapshot(HostStateSnapshot snapshot)
    {
        lock (_sync)
        {
            _snapshot = snapshot;
        }
    }

    private void LogConnectionChange(bool connected, string? errorMessage)
    {
        if (_hasObservedState && connected == _lastConnected)
            return;

        _hasObservedState = true;
        _lastConnected = connected;
        if (connected)
        {
            Console.Error.WriteLine("[HARNESS] Aspire MCP connected.");
        }
        else
        {
            var suffix = string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : $" {errorMessage}";
            Console.Error.WriteLine($"[HARNESS] Aspire MCP disconnected.{suffix}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
