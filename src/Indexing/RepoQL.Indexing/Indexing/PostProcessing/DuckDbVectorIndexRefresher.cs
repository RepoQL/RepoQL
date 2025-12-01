using System.Diagnostics;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Refreshes the DuckDB-backed vector index by invoking <see cref="DuckDbGraphStore.RefreshDocumentEmbeddings"/>.
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
            _logger.LogDebug("Embedding refresh skipped - provider disabled.");
            return;
        }

        _logger.LogInformation("Embedding refresh starting...");
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
            store.RefreshDocumentEmbeddings(_embeddingProvider, cancellationToken);
        }

        await RemoveDanglingEmbeddingsAsync(connection, cancellationToken).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation("Embedding refresh completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }

    private static async Task RemoveDanglingEmbeddingsAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
                          DELETE FROM document_embedding
                          WHERE doc_id NOT IN (SELECT id FROM node WHERE kind = 'document')
                             OR node_id NOT IN (SELECT id FROM node);
                          """;
        await Task.Run(() => cmd.ExecuteNonQuery(), cancellationToken).ConfigureAwait(false);
    }
}
