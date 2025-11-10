using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Data.DuckDB;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Coordinates post-index vector refreshes. The heavy lifting is delegated to an <see cref="IVectorIndexRefresher"/>.
/// </summary>
public sealed class VectorIndexCoordinator : IVectorIndexCoordinator, IDisposable
{
    private readonly IVectorIndexRefresher _refresher;
    private readonly ILogger<VectorIndexCoordinator> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _lastRefreshedEpoch = long.MinValue;
    private volatile bool _needsRefresh;

    public VectorIndexCoordinator(
        IDuckDBConnectionFactory connectionFactory,
        IEmbeddingProvider embeddingProvider,
        ILogger<VectorIndexCoordinator>? logger = null)
        : this(new DuckDbVectorIndexRefresher(connectionFactory, embeddingProvider), logger)
    {
    }

    internal VectorIndexCoordinator(
        IVectorIndexRefresher refresher,
        ILogger<VectorIndexCoordinator>? logger = null)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _logger = logger ?? NullLogger<VectorIndexCoordinator>.Instance;
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count == 0)
            return Task.CompletedTask;

        _needsRefresh = true;
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(IndexItem item, CancellationToken cancellationToken)
    {
        var epoch = item.Epoch;
        if (!_needsRefresh && Interlocked.Read(ref _lastRefreshedEpoch) == epoch)
            return;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_needsRefresh && Interlocked.Read(ref _lastRefreshedEpoch) == epoch)
                return;

            await RefreshEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastRefreshedEpoch, epoch);
            _needsRefresh = false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshEmbeddingsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Refreshing document embeddings to keep vector index current.");
        await _refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
    }
}
