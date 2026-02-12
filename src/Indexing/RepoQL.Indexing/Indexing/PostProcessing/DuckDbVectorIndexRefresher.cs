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
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger<DuckDbVectorIndexRefresher> _logger;

    public DuckDbVectorIndexRefresher(
        DuckDbDataStore dataStore,
        IEmbeddingProvider embeddingProvider,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger<DuckDbVectorIndexRefresher>? logger = null)
    {
        if (dataStore is null) throw new ArgumentNullException(nameof(dataStore));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _embeddingMode = embeddingMode;
        _logger = logger ?? NullLogger<DuckDbVectorIndexRefresher>.Instance;
        _refresher = new EmbeddingRefresher(dataStore, embeddingMode, logger as ILogger<EmbeddingRefresher>);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!CanRunRefresh())
            return;

        _logger.LogInformation("Embedding refresh starting (mode=full, model={Model}, dim={Dim})...", _embeddingProvider.Model, _embeddingProvider.Dimension);
        var sw = Stopwatch.StartNew();

        await _refresher.RefreshAsync(_embeddingProvider, cancellationToken).ConfigureAwait(false);
        _refresher.RemoveDangling();

        sw.Stop();
        _logger.LogInformation("Embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }

    public async Task RefreshAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds is null)
            throw new ArgumentNullException(nameof(documentIds));

        if (documentIds.Count == 0)
        {
            _logger.LogDebug("Targeted embedding refresh skipped - no document ids");
            return;
        }

        if (!CanRunRefresh())
            return;

        _logger.LogInformation("Embedding refresh starting (mode=targeted, docs={DocCount}, model={Model}, dim={Dim})...",
            documentIds.Count, _embeddingProvider.Model, _embeddingProvider.Dimension);
        var sw = Stopwatch.StartNew();

        await _refresher.RefreshAsync(_embeddingProvider, documentIds, cancellationToken).ConfigureAwait(false);
        _refresher.RemoveDangling();

        sw.Stop();
        _logger.LogInformation("Targeted embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }

    private bool CanRunRefresh()
    {
        // Full embeddings require Full or Hybrid mode
        if (!_embeddingMode.IncludesFull() && !_embeddingMode.IsHybrid())
        {
            _logger.LogDebug("Full embedding refresh skipped - mode={Mode}", _embeddingMode);
            return false;
        }

        if (_embeddingProvider.Enabled)
            return true;

        _logger.LogInformation("Embedding refresh skipped - provider disabled (model={Model}).", _embeddingProvider.Model);
        return false;
    }
}
