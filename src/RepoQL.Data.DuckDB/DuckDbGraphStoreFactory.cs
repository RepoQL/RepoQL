using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Metrics;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Factory for creating <see cref="DuckDbGraphStore"/> instances with proper dependency injection.
/// </summary>
public sealed class DuckDbGraphStoreFactory : IDuckDbGraphStoreFactory
{
    private readonly IndexingMetrics? _metrics;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ILogger<DuckDbGraphStore> _logger;
    private readonly string? _repoRootPath;

    public DuckDbGraphStoreFactory(
        IndexingMetrics? metrics = null,
        IEmbeddingProvider? embeddingProvider = null,
        ILogger<DuckDbGraphStore>? logger = null,
        string? repoRootPath = null)
    {
        _metrics = metrics;
        _embeddingProvider = embeddingProvider;
        _logger = logger ?? NullLogger<DuckDbGraphStore>.Instance;
        _repoRootPath = repoRootPath;
    }

    public DuckDbGraphStore Create(DuckDBConnection connection, IEnumerable<FormatSqlScript>? formatSchemaScripts = null)
    {
        return new DuckDbGraphStore(
            connection,
            _metrics,
            enableExtensions: true,
            registerUdfs: true,
            logger: _logger,
            embeddingProvider: _embeddingProvider,
            formatSchemaScripts: formatSchemaScripts,
            repoRootPath: _repoRootPath);
    }
}
