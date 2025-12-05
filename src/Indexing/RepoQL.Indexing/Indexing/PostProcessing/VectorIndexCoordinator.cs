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
/// <remarks>
/// <para><strong>Environment Variables:</strong></para>
/// <list type="bullet">
///   <item><c>REPOQL_EMBED_CONCURRENCY</c> - Max concurrent refresh operations (default: 1). Increase to allow parallel embedding batches for higher throughput.</item>
/// </list>
/// </remarks>
public sealed class VectorIndexCoordinator : IVectorIndexCoordinator, IDisposable
{
    private static readonly int RefreshConcurrency = GetRefreshConcurrency();
    private readonly IVectorIndexRefresher _refresher;
    private readonly ILogger<VectorIndexCoordinator> _logger;
    private readonly SemaphoreSlim _refreshGate = new(RefreshConcurrency, RefreshConcurrency);
    private long _lastRefreshedEpoch = long.MinValue;
    private volatile bool _needsRefresh;

    private static int GetRefreshConcurrency()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_CONCURRENCY"), out var c) && c > 0)
            return c;
        return 2; // Default to 2 concurrent batches for better throughput
    }

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
        _logger.LogDebug("Vector index refresh triggered");
        await _refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
    }

    /// <summary>
    /// Gets the last epoch that was refreshed for embeddings.
    /// </summary>
    public long GetLastRefreshedEpoch() => Interlocked.Read(ref _lastRefreshedEpoch);

    /// <summary>
    /// Gets whether the coordinator needs a refresh (e.g., due to deletes).
    /// </summary>
    public bool GetNeedsRefresh() => _needsRefresh;
}
