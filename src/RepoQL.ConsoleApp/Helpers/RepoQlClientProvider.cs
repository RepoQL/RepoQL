using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Helpers;

/// <summary>
/// Provides a shared RepoQL gRPC client instance and supports asynchronous warm-up.
/// </summary>
internal sealed class RepoQlClientProvider : IAsyncDisposable
{
    private readonly Lazy<Task<IRepoQlClient>> _clientTask;
    private readonly ILogger<RepoQlClientProvider> _logger;

    public RepoQlClientProvider(ILogger<RepoQlClientProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<RepoQlClientProvider>.Instance;
        _clientTask = new Lazy<Task<IRepoQlClient>>(CreateClientAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Begin establishing the client connection if it has not already started.
    /// </summary>
    public Task<IRepoQlClient> EnsureStarted() => _clientTask.Value;

    /// <summary>
    /// Await the client, propagating cancellation to the awaiting caller but not the underlying task.
    /// </summary>
    public async ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _clientTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_clientTask.IsValueCreated)
            return;

        try
        {
            var client = await _clientTask.Value.ConfigureAwait(false);
            var _ = client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose RepoQL client during shutdown");
        }
    }

    private static async Task<IRepoQlClient> CreateClientAsync()
    {
        var client = await RepoQlClient.CreateAsync().ConfigureAwait(false);
        return client;
    }
}
