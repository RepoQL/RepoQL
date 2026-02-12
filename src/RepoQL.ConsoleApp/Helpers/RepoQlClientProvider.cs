using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Helpers;

/// <summary>
/// Provides a shared RepoQL gRPC client instance and supports asynchronous warm-up.
/// </summary>
internal sealed class RepoQlClientProvider : IAsyncDisposable
{
    private readonly ILogger<RepoQlClientProvider> _logger;
    private readonly object _sync = new();
    private RepoQlClientOptions _options;
    private Task<IRepoQlClient>? _clientTask;

    public RepoQlClientProvider(ILogger<RepoQlClientProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<RepoQlClientProvider>.Instance;
        _options = new RepoQlClientOptions();
    }

    /// <summary>
    /// Begin establishing the client connection if it has not already started.
    /// </summary>
    public Task<IRepoQlClient> EnsureStarted() => GetClientAsync().AsTask();

    /// <summary>
    /// Await the client, propagating cancellation to the awaiting caller but not the underlying task.
    /// </summary>
    public async ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        Task<IRepoQlClient> task;
        lock (_sync)
        {
            if (_clientTask is { IsCompleted: true, IsFaulted: true } || _clientTask is { IsCompleted: true, IsCanceled: true })
            {
                _clientTask = null; // drop a failed task so we can recreate
            }

            _clientTask ??= CreateClientAsync(cancellationToken);
            task = _clientTask;
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RepoRootNotFoundException ex)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_clientTask, task))
                {
                    _clientTask = null; // allow retry after user provides a path
                }
            }

            var cwd = Directory.GetCurrentDirectory();
            var instructionPath = Path.GetFullPath(cwd);
            throw new InvalidOperationException(
                $"No repository markers (.git or .repoql) were found starting at '{ex.SearchedFrom}'. " +
                $"Current working directory: '{instructionPath}'. " +
                $"Use ::repo[{instructionPath}] to set the repository root, then retry.",
                ex);
        }
        catch
        {
            // On any connection failure, drop the cached task so callers can retry and recreate the client.
            lock (_sync)
            {
                if (ReferenceEquals(_clientTask, task))
                {
                    _clientTask = null;
                }
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<IRepoQlClient>? task;
        lock (_sync)
        {
            task = _clientTask;
            _clientTask = null;
        }

        if (task is null)
            return;

        try
        {
            var client = await task.ConfigureAwait(false);
            var _ = client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose RepoQL client during shutdown");
        }
    }

    public void SetWorkingDirectory(string repoRootPath)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath))
            throw new ArgumentException("Repository root path cannot be null or empty", nameof(repoRootPath));

        var resolved = Path.GetFullPath(repoRootPath);
        lock (_sync)
        {
            _options = new RepoQlClientOptions
            {
                RepositoryPath = resolved,
                SocketPath = _options.SocketPath,
                DefaultTimeout = _options.DefaultTimeout
            };
            _clientTask = null; // force re-create with new path
        }
    }

    private Task<IRepoQlClient> CreateClientAsync(CancellationToken cancellationToken)
        => RepoQlClient.CreateAsync(_options, _logger, cancellationToken);
}
