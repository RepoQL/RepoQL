using RepoQL.Protocol;

namespace RepoQL.Web.Services;

/// <summary>
/// Provides a shared RepoQL client instance and tracks connection state for UI consumers.
/// </summary>
internal sealed class RepoQlConnectionManager : IAsyncDisposable
{
    private readonly ILogger<RepoQlConnectionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private IRepoQlClient? _client;
    private ConnectionState _state;

    public RepoQlConnectionManager(ILogger<RepoQlConnectionManager> logger)
    {
        _logger = logger;
        _state = ConnectionState.Create(false, "Not connected");
    }

    public event EventHandler<ConnectionState>? StateChanged;

    public ConnectionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Ensure a RepoQL client is available, creating one if necessary.
    /// </summary>
    public async ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        if (_client is { } existing)
            return existing;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } ready)
                return ready;

            UpdateState(ConnectionState.Create(false, "Connecting to RepoQL host…"));
            var client = await RepoQlClient.CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            _client = client;
            UpdateState(ConnectionState.Create(true, "Connected"));
            return client;
        }
        catch (OperationCanceledException)
        {
            UpdateState(ConnectionState.Create(false, "Connection attempt cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RepoQL connection attempt failed");
            UpdateState(ConnectionState.Create(false, $"Connection failed: {ex.GetBaseException().Message}"));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a background warm-up attempt that ignores failures.
    /// </summary>
    public void StartWarmup()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GetClientAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RepoQL warmup attempt failed");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void UpdateState(ConnectionState state)
    {
        ConnectionState snapshot;
        lock (_stateGate)
        {
            _state = state with { UpdatedAt = DateTimeOffset.UtcNow };
            snapshot = _state;
        }
        StateChanged?.Invoke(this, snapshot);
    }

    internal sealed record ConnectionState(bool IsConnected, string Description, DateTimeOffset UpdatedAt)
    {
        public static ConnectionState Create(bool isConnected, string description)
            => new(isConnected, description, DateTimeOffset.UtcNow);
    }
}
