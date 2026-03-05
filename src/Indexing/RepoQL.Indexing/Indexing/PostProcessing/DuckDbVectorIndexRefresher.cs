using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Configuration;
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
    private readonly IContextualEmbeddingProvider? _contextualProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger _logger;

    public DuckDbVectorIndexRefresher(
        DuckDbDataStore dataStore,
        IEmbeddingProvider embeddingProvider,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger? logger = null,
        RepoQlConfig.EmbeddingSettings? embeddingSettings = null,
        IContextualEmbeddingProvider? contextualProvider = null)
    {
        if (dataStore is null) throw new ArgumentNullException(nameof(dataStore));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _contextualProvider = contextualProvider;
        _embeddingMode = embeddingMode;
        _logger = logger ?? NullLogger<DuckDbVectorIndexRefresher>.Instance;
        _refresher = new EmbeddingRefresher(dataStore, embeddingMode, logger, embeddingSettings, contextualProvider);
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!CanRunRefresh())
            return false;

        _logger.LogInformation("Embedding refresh starting (mode=full, model={Model}, dim={Dim})...", _embeddingProvider.Model, _embeddingProvider.Dimension);
        var sw = Stopwatch.StartNew();

        var refreshed = await _refresher.RefreshAsync(_embeddingProvider, cancellationToken).ConfigureAwait(false);
        var danglingRemoved = _refresher.RemoveDangling();

        sw.Stop();
        _logger.LogInformation("Embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
        return refreshed || danglingRemoved > 0;
    }

    public async Task<bool> RefreshAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds is null)
            throw new ArgumentNullException(nameof(documentIds));

        if (documentIds.Count == 0)
        {
            _logger.LogDebug("Targeted embedding refresh skipped - no document ids");
            return false;
        }

        if (!CanRunRefresh())
            return false;

        _logger.LogInformation("Embedding refresh starting (mode=targeted, docs={DocCount}, model={Model}, dim={Dim})...",
            documentIds.Count, _embeddingProvider.Model, _embeddingProvider.Dimension);
        var sw = Stopwatch.StartNew();

        var refreshed = await _refresher.RefreshAsync(_embeddingProvider, documentIds, cancellationToken).ConfigureAwait(false);
        var danglingRemoved = _refresher.RemoveDangling();

        sw.Stop();
        _logger.LogInformation("Targeted embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
        return refreshed || danglingRemoved > 0;
    }

    private bool CanRunRefresh()
    {
        // Full embeddings require Full or Hybrid mode
        if (!_embeddingMode.IncludesFull() && !_embeddingMode.IsHybrid())
        {
            _logger.LogDebug("Full embedding refresh skipped - mode={Mode}", _embeddingMode);
            return false;
        }

        // Either provider being available is sufficient
        if (_contextualProvider is { Enabled: true })
            return true;

        if (_embeddingProvider.Enabled)
            return true;

        _logger.LogInformation("Embedding refresh skipped - no provider available (flat={Model}, contextual={Contextual}).",
            _embeddingProvider.Model, _contextualProvider?.Model ?? "none");
        return false;
    }
}
