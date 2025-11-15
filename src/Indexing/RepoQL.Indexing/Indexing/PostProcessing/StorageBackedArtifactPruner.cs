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
    private readonly IDuckDBConnectionFactory _connectionFactory;
    private readonly ILogger<StorageBackedArtifactPruner> _logger;
    private readonly Func<bool> _isReindexingAccessor;

    public StorageBackedArtifactPruner(
        IDuckDBConnectionFactory connectionFactory,
        ILogger<StorageBackedArtifactPruner>? logger = null)
        : this(connectionFactory, static () => false, logger)
    {
    }

    public StorageBackedArtifactPruner(
        IDuckDBConnectionFactory connectionFactory,
        Func<bool> isReindexingAccessor,
        ILogger<StorageBackedArtifactPruner>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? NullLogger<StorageBackedArtifactPruner>.Instance;
        _isReindexingAccessor = isReindexingAccessor ?? throw new ArgumentNullException(nameof(isReindexingAccessor));
    }

    public async Task<PruningResult> PruneAsync(IReadOnlyCollection<IndexItem> pendingItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingItems);

        if (!_isReindexingAccessor())
        {
            _logger.LogDebug("Pruning skipped because no reindex operation is active.");
            return PruningResult.None;
        }

        // Build the set of URIs that were observed during the latest indexing sweep.
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pendingItems)
        {
            live.Add(item.Uri.AbsoluteUri);
        }

        var stale = new List<RepoUri>();

        await using var connection = _connectionFactory.CreateConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT uri FROM node WHERE kind = 'document'";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var uriText = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(uriText))
                continue;

            if (live.Contains(uriText))
                continue;

            if (RepoUri.TryParse(uriText, out var parsed) && parsed is not null)
            {
                stale.Add(parsed);
            }
        }

        if (stale.Count == 0)
            return PruningResult.None;

        _logger.LogInformation("Pruning identified {Count} stale documents.", stale.Count);
        return new PruningResult(stale);
    }
}
