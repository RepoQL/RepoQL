using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Compares the set of documents recorded in DuckDB with the URIs observed during the latest
/// indexing epoch. Any stored document that was not observed is treated as stale and should be
/// deleted before analysis resumes.
/// </summary>
public sealed class StorageBackedArtifactPruner : IArtifactPruner
{
    private readonly DuckDbDataStore _store;
    private readonly ILogger<StorageBackedArtifactPruner> _logger;
    private readonly Func<bool> _isReindexingAccessor;

    public StorageBackedArtifactPruner(
        DuckDbDataStore store,
        ILogger<StorageBackedArtifactPruner>? logger = null)
        : this(store, static () => false, logger)
    {
    }

    public StorageBackedArtifactPruner(
        DuckDbDataStore store,
        Func<bool> isReindexingAccessor,
        ILogger<StorageBackedArtifactPruner>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<StorageBackedArtifactPruner>.Instance;
        _isReindexingAccessor = isReindexingAccessor ?? throw new ArgumentNullException(nameof(isReindexingAccessor));
    }

    public Task<PruningResult> PruneAsync(IReadOnlyCollection<IndexItem> pendingItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingItems);

        if (!_isReindexingAccessor())
        {
            _logger.LogDebug("Pruning skipped because no reindex operation is active.");
            return Task.FromResult(PruningResult.None);
        }

        // Build the set of URIs that were observed during the latest indexing sweep.
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pendingItems)
        {
            live.Add(item.Uri.AbsoluteUri);
        }

        var stale = _store.Read(
            "SELECT uri FROM node WHERE kind = 'document'",
            reader =>
            {
                var uriText = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(uriText))
                    return null;

                if (live.Contains(uriText))
                    return null;

                if (RepoUri.TryParse(uriText, out var parsed) && parsed is not null)
                    return parsed;

                return null;
            })
            .Where(u => u is not null)
            .Cast<RepoUri>()
            .ToList();

        if (stale.Count == 0)
            return Task.FromResult(PruningResult.None);

        _logger.LogInformation("Pruning identified {Count} stale documents.", stale.Count);
        return Task.FromResult(new PruningResult(stale));
    }
}
