using System.Data;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Thread-safe DuckDB data store with lazy schema initialization.
/// All database access should go through Read/WriteTransaction methods.
/// </summary>
public sealed class DuckDbDataStore : IDisposable
{
    private readonly DuckDBConnection _reader;
    private readonly DuckDBConnection _writer;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ILogger _logger;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IReadOnlyList<FormatSqlScript> _formatSchemaScripts;
    private readonly bool _isInMemory;
    private bool _schemaInitialized;
    private bool _disposed;

    public DuckDbDataStore(
        string? path = null,
        IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null,
        ILogger<DuckDbDataStore>? logger = null)
    {
        _logger = logger ?? NullLogger<DuckDbDataStore>.Instance;
        _embeddingProvider = embeddingProvider;
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? [];
        _isInMemory = path is null || path == ":memory:";

        if (_isInMemory)
        {
            _writer = new DuckDBConnection("Data Source=:memory:");
            _writer.Open();
            _reader = _writer;
        }
        else
        {
            _writer = new DuckDBConnection($"Data Source={path};ACCESS_MODE=READ_WRITE");
            _writer.Open();
            _reader = new DuckDBConnection($"Data Source={path};ACCESS_MODE=READ_ONLY");
            _reader.Open();
        }
    }

    public IReadOnlyList<T> Read<T>(string sql, Func<IDataRecord, T> map)
    {
        EnsureSchema();
        _lock.EnterReadLock();
        try
        {
            using var cmd = _reader.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var results = new List<T>();
            while (reader.Read())
                results.Add(map(reader));
            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public T? ReadScalar<T>(string sql)
    {
        EnsureSchema();
        _lock.EnterReadLock();
        try
        {
            using var cmd = _reader.CreateCommand();
            cmd.CommandText = sql;
            var result = cmd.ExecuteScalar();
            if (result is null or DBNull) return default;
            if (result is T typed) return typed;
            return (T)Convert.ChangeType(result, typeof(T));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void WriteTransaction(Action<DuckDBConnection, DuckDBTransaction> work)
    {
        EnsureSchema();
        _lock.EnterWriteLock();
        try
        {
            using var tx = _writer.BeginTransaction();
            work(_writer, tx);
            tx.Commit();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public T WriteTransaction<T>(Func<DuckDBConnection, DuckDBTransaction, T> work)
    {
        EnsureSchema();
        _lock.EnterWriteLock();
        try
        {
            using var tx = _writer.BeginTransaction();
            var result = work(_writer, tx);
            tx.Commit();
            return result;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void EnsureSchema()
    {
        if (_schemaInitialized) return;

        _lock.EnterWriteLock();
        try
        {
            if (_schemaInitialized) return;

            if (!_isInMemory)
                _writer.Execute("SET wal_autocheckpoint = '256MB';");

            _writer.Execute("CREATE TABLE IF NOT EXISTS repo_metadata(key TEXT PRIMARY KEY, value TEXT, updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP);");

            RepositoryUserDefinedFunctions.RegisterAll(_writer, _embeddingProvider);

            ExecuteSqlResource("Tables/artifact.sql");
            ExecuteSqlResource("Tables/node.sql");
            ExecuteSqlResource("Tables/span.sql");
            ExecuteSqlResource("Tables/edge.sql");
            ExecuteSqlResource("Macros/entities_by_uri.sql");
            ExecuteSqlResource("Macros/json_extract_string_array.sql");
            ExecuteSqlResource("Tables/annotation.sql");
            ExecuteSqlResource("Views/annotations.sql");
            ExecuteSqlResource("Macros/annotations_for.sql");
            ExecuteSqlResource("Macros/annotations_all.sql");
            ExecuteSqlResource("Macros/glob_match.sql");
            ExecuteSqlResource("Tables/document_embedding.sql");
            ExecuteSqlResource("Views/repo_index.sql");
            ExecuteSqlResource("Macros/snippet.sql");
            ExecuteSqlResource("Macros/node_primary_fragment.sql");
            ExecuteSqlResource("Macros/xray_documents.sql");
            ExecuteSqlResource("Macros/xray_items.sql");
            ExecuteSqlResource("Macros/xray_lines.sql");
            ExecuteSqlResource("Tables/document_search.sql");
            ExecuteSqlResource("Macros/search.sql");
            ExecuteSqlResource("Macros/hybrid_search.sql");
            ExecuteSqlResource("Tables/file_system_mount.sql");

            foreach (var script in _formatSchemaScripts)
            {
                try { _writer.Execute(script.Sql); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to apply format schema {Id}", script.Identifier); }
            }

            if (!_isInMemory)
                RepositoryUserDefinedFunctions.RegisterAll(_reader, _embeddingProvider);

            _schemaInitialized = true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void ExecuteSqlResource(string relativePath)
    {
        var normalized = relativePath.Replace('/', '.').Replace('\\', '.');
        var resourceName = $"{typeof(DuckDbDataStore).Namespace}.Schema.{normalized}";
        using var stream = typeof(DuckDbDataStore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        _writer.Execute(reader.ReadToEnd());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lock.EnterWriteLock();
        try
        {
            if (!_isInMemory) _reader.Dispose();
            _writer.Dispose();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}
