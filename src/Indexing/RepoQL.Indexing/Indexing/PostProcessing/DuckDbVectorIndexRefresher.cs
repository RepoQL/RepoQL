using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Refreshes the DuckDB-backed vector index by delegating to <see cref="EmbeddingRefresher"/>.
/// Uses pipelined producer-consumer pattern for optimal throughput.
/// </summary>
public sealed class DuckDbVectorIndexRefresher : IVectorIndexRefresher
{
    private readonly EmbeddingRefresher _refresher;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<DuckDbVectorIndexRefresher> _logger;

    public DuckDbVectorIndexRefresher(
        DuckDbDataStore dataStore,
        IEmbeddingProvider embeddingProvider,
        ILogger<DuckDbVectorIndexRefresher>? logger = null)
    {
        if (dataStore is null) throw new ArgumentNullException(nameof(dataStore));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _logger = logger ?? NullLogger<DuckDbVectorIndexRefresher>.Instance;
        _refresher = new EmbeddingRefresher(dataStore, logger as ILogger<EmbeddingRefresher>);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_embeddingProvider.Enabled)
        {
            _logger.LogInformation("Embedding refresh skipped - provider disabled (model={Model}).", _embeddingProvider.Model);
            return;
        }

        _logger.LogInformation("Embedding refresh starting (model={Model}, dim={Dim})...", _embeddingProvider.Model, _embeddingProvider.Dimension);
        var sw = Stopwatch.StartNew();

        // Refresh document embeddings using the refresher
        await _refresher.RefreshAsync(_embeddingProvider, cancellationToken).ConfigureAwait(false);

        // Remove dangling embeddings AFTER the refresh completes
        _refresher.RemoveDangling();

        sw.Stop();
        _logger.LogInformation("Embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }
}
