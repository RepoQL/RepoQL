using System.Diagnostics;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Refreshes the DuckDB-backed vector index by invoking <see cref="DuckDbGraphStore.RefreshDocumentEmbeddingsAsync"/>.
/// Uses pipelined producer-consumer pattern for optimal throughput.
/// </summary>
public sealed class DuckDbVectorIndexRefresher : IVectorIndexRefresher
{
    private readonly IDuckDBConnectionFactory _connectionFactory;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<DuckDbVectorIndexRefresher> _logger;

    public DuckDbVectorIndexRefresher(
        IDuckDBConnectionFactory connectionFactory,
        IEmbeddingProvider embeddingProvider,
        ILogger<DuckDbVectorIndexRefresher>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _logger = logger ?? NullLogger<DuckDbVectorIndexRefresher>.Instance;
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

        await using var connection = _connectionFactory.CreateConnection();
        if (connection.State == System.Data.ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        // Create a logger for DuckDbGraphStore from our logger factory
        var graphStoreLogger = _logger as ILogger<DuckDbGraphStore>
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DuckDbGraphStore>.Instance;

        using (var store = new DuckDbGraphStore(
                   connection,
                   metrics: null,
                   enableExtensions: false,
                   registerUdfs: false,
                   logger: graphStoreLogger,
                   embeddingProvider: _embeddingProvider))
        {
            await store.RefreshDocumentEmbeddingsAsync(_embeddingProvider, cancellationToken).ConfigureAwait(false);
        }

        // Remove dangling embeddings AFTER the refresh completes, within a transaction
        // to avoid race conditions with concurrent inserts
        await RemoveDanglingEmbeddingsAsync(connection, cancellationToken).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation("Embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }

    private async Task RemoveDanglingEmbeddingsAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            // Use a subquery snapshot approach to minimize race window:
            // First capture the current set of valid node IDs, then delete based on that snapshot
            cmd.CommandText = """
                              WITH valid_docs AS (SELECT id FROM node WHERE kind = 'document'),
                                   valid_nodes AS (SELECT id FROM node)
                              DELETE FROM document_embedding
                              WHERE doc_id NOT IN (SELECT id FROM valid_docs)
                                 OR node_id NOT IN (SELECT id FROM valid_nodes);
                              """;
            var deleted = await Task.Run(() => cmd.ExecuteNonQuery(), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
            {
                _logger.LogInformation("Removed {Count} dangling embeddings", deleted);
            }
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "Failed to remove dangling embeddings");
            throw;
        }
    }
}
