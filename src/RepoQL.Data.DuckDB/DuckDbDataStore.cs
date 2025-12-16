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
/// Includes automatic detection and recovery from database corruption.
/// </summary>
public sealed class DuckDbDataStore : IDisposable
{
    private readonly string? _path;
    private DuckDBConnection _reader;
    private DuckDBConnection _writer;
    private readonly SemaphoreSlim _lock = new(1, 1); // DuckDB connections aren't thread-safe for concurrent commands
    private readonly ILogger _logger;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IReadOnlyList<FormatSqlScript> _formatSchemaScripts;
    private readonly bool _isInMemory;
    private bool _schemaInitialized;
    private bool _disposed;
    private bool _databaseInvalidated;
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailuresBeforeRecovery = 3;

    /// <summary>
    /// Indicates that a database recovery occurred and data may have been lost.
    /// Check this flag on startup or after operations to determine if reindex is needed.
    /// </summary>
    public bool RecoveryOccurred { get; private set; }

    public DuckDbDataStore(
        string? path = null,
        IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null,
        ILogger<DuckDbDataStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<DuckDbDataStore>.Instance;
        _embeddingProvider = embeddingProvider;
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? [];
        _isInMemory = path is null || path == ":memory:";

        _logger.LogDebug("[DuckDB] Initializing data store (path={Path}, inMemory={IsInMemory})",
            _isInMemory ? ":memory:" : path, _isInMemory);

        InitializeConnections();
    }

    private void InitializeConnections()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (_isInMemory)
        {
            _logger.LogDebug("[DuckDB] Opening in-memory database");
            _writer = new DuckDBConnection("Data Source=:memory:");
            _writer.Open();
            ApplyConnectionConfiguration(_writer);
            _reader = _writer;
        }
        else
        {
            _logger.LogDebug("[DuckDB] Opening database file: {Path}", _path);

            // Check if database file exists and get its size
            if (File.Exists(_path))
            {
                var fileInfo = new FileInfo(_path!);
                _logger.LogDebug("[DuckDB] Existing database file: {Size:N0} bytes, modified {Modified}",
                    fileInfo.Length, fileInfo.LastWriteTimeUtc);
            }
            else
            {
                _logger.LogInformation("[DuckDB] Creating new database file: {Path}", _path);
            }

            try
            {
                _writer = new DuckDBConnection($"Data Source={_path};ACCESS_MODE=READ_WRITE");
                _writer.Open();
                ApplyConnectionConfiguration(_writer);
                _logger.LogDebug("[DuckDB] Writer connection opened");

                _reader = new DuckDBConnection($"Data Source={_path};ACCESS_MODE=READ_ONLY");
                _reader.Open();
                ApplyConnectionConfiguration(_reader);
                _logger.LogDebug("[DuckDB] Reader connection opened");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DuckDB] Failed to open database connections for {Path}", _path);
                throw;
            }
        }

        _logger.LogDebug("[DuckDB] Connections initialized in {ElapsedMs}ms", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Applies memory, threading, and storage settings to a DuckDB connection.
    /// Settings are read from environment variables with sensible defaults for
    /// low-memory operation (targeting developer laptops with multiple agents).
    /// </summary>
    /// <remarks>
    /// Environment variables:
    /// <list type="bullet">
    ///   <item><c>DUCKDB_MEMORY_LIMIT</c> - Max memory (default: 8GB)</item>
    ///   <item><c>DUCKDB_THREADS</c> - Worker threads (default: 1)</item>
    ///   <item><c>DUCKDB_TEMP_DIRECTORY</c> - Spill directory (default: next to database file)</item>
    /// </list>
    /// </remarks>
    private static int _configApplyCount;
    private void ApplyConnectionConfiguration(DuckDBConnection connection)
    {
        var count = Interlocked.Increment(ref _configApplyCount);

        // Set memory limit to prevent runaway allocations
        var limit = Environment.GetEnvironmentVariable("DUCKDB_MEMORY_LIMIT") ?? "8GB";
        ExecSetting(connection, $"SET memory_limit='{limit}';");

        // Disable object cache - defaults to 80% of RAM which is far too aggressive
        ExecSetting(connection, "SET enable_object_cache=false;");

        // Disable insertion order preservation to reduce memory overhead
        ExecSetting(connection, "SET preserve_insertion_order=false;");

        var threads = Environment.GetEnvironmentVariable("DUCKDB_THREADS") ?? "1";
        ExecSetting(connection, $"SET threads={threads};");

        // Return freed memory to OS more aggressively (default 128MB holds too long)
        ExecSetting(connection, "SET allocator_flush_threshold='64MB';");

        // Ensure spills go to a deterministic temp directory (relative to database, not CWD)
        var tempDir = Environment.GetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY");
        if (string.IsNullOrEmpty(tempDir) && !string.IsNullOrEmpty(_path))
        {
            // Default to temp directory next to the database file
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(_path));
            tempDir = Path.Combine(dbDir ?? ".", "index.duckdb.tmp");
        }
        tempDir ??= ".repoql/index.duckdb.tmp";
        var tempDirPath = Path.GetFullPath(tempDir).Replace("\\", "/", StringComparison.Ordinal);
        Directory.CreateDirectory(tempDirPath);
        ExecSetting(connection, $"SET temp_directory='{tempDirPath}';");

        // Log settings on first apply
        if (count == 1)
        {
            _logger.LogInformation("[DuckDB] Configuration: memory_limit={Limit}, threads={Threads}, object_cache=false, flush_threshold=64MB, temp_dir={TempDir}",
                limit, threads, tempDirPath);
        }
    }

    private static void ExecSetting(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<T> Read<T>(string sql, Func<IDataRecord, T> map)
    {
        EnsureSchema();

        // If database is invalidated, attempt recovery before proceeding
        if (_databaseInvalidated)
        {
            CheckAndRecoverIfNeeded();
        }

        _lock.Wait();
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
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            HandleFatalError(ex, "Read");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public T? ReadScalar<T>(string sql)
    {
        EnsureSchema();

        // If database is invalidated, attempt recovery before proceeding
        if (_databaseInvalidated)
        {
            CheckAndRecoverIfNeeded();
        }

        _lock.Wait();
        try
        {
            using var cmd = _reader.CreateCommand();
            cmd.CommandText = sql;
            var result = cmd.ExecuteScalar();
            if (result is null or DBNull) return default;
            if (result is T typed) return typed;
            return (T)Convert.ChangeType(result, typeof(T));
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            HandleFatalError(ex, "ReadScalar");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void WriteTransaction(Action<DuckDBConnection, DuckDBTransaction> work)
    {
        EnsureSchema();

        // If database is invalidated, attempt recovery before proceeding
        if (_databaseInvalidated)
        {
            CheckAndRecoverIfNeeded();
        }

        _lock.Wait();
        DuckDBTransaction? tx = null;
        try
        {
            tx = _writer.BeginTransaction();
            work(_writer, tx);
            tx.Commit();
            tx = null; // Mark as committed so we don't rollback
            Interlocked.Exchange(ref _consecutiveFailures, 0); // Reset on success
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            TryRollback(tx);
            HandleFatalError(ex, "WriteTransaction");
            throw;
        }
        catch (DuckDBException ex) when (IsWriteConflictError(ex))
        {
            TryRollback(tx);
            HandleWriteConflict(ex, "WriteTransaction");
            throw;
        }
        catch (Exception)
        {
            TryRollback(tx);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void TryRollback(DuckDBTransaction? tx)
    {
        if (tx is null) return;
        try
        {
            tx.Rollback();
        }
        catch (Exception rollbackEx)
        {
            // Rollback failed - connection is in inconsistent state, must recover
            _logger.LogError(rollbackEx, "[DuckDB] Transaction rollback failed - connection corrupted, marking for recovery");
            _databaseInvalidated = true;
        }
    }

    public T WriteTransaction<T>(Func<DuckDBConnection, DuckDBTransaction, T> work)
    {
        EnsureSchema();

        // If database is invalidated, attempt recovery before proceeding
        if (_databaseInvalidated)
        {
            CheckAndRecoverIfNeeded();
        }

        _lock.Wait();
        DuckDBTransaction? tx = null;
        try
        {
            tx = _writer.BeginTransaction();
            var result = work(_writer, tx);
            tx.Commit();
            tx = null; // Mark as committed so we don't rollback
            Interlocked.Exchange(ref _consecutiveFailures, 0); // Reset on success
            return result;
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            TryRollback(tx);
            HandleFatalError(ex, "WriteTransaction<T>");
            throw;
        }
        catch (DuckDBException ex) when (IsWriteConflictError(ex))
        {
            TryRollback(tx);
            HandleWriteConflict(ex, "WriteTransaction<T>");
            throw;
        }
        catch (Exception)
        {
            TryRollback(tx);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks if the database is in an invalidated state and attempts recovery.
    /// </summary>
    private void CheckAndRecoverIfNeeded()
    {
        if (!_databaseInvalidated) return;

        _lock.Wait();
        try
        {
            if (!_databaseInvalidated) return; // Double-check after acquiring lock

            _logger.LogWarning("[DuckDB] Database was invalidated, attempting recovery...");
            AttemptRecovery();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Attempts to recover from a fatal database error by recreating connections.
    /// </summary>
    private void AttemptRecovery()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[DuckDB] Starting database recovery...");

        try
        {
            // Close existing connections
            _logger.LogDebug("[DuckDB] Closing existing connections...");
            try { _writer?.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error closing writer"); }
            try { if (!_isInMemory) _reader?.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error closing reader"); }
            try { _writer?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing writer"); }
            try { if (!_isInMemory) _reader?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing reader"); }

            // For file-based databases, check if we need to delete corrupted files
            if (!_isInMemory && _path is not null)
            {
                var walPath = _path + ".wal";
                if (File.Exists(walPath))
                {
                    _logger.LogWarning("[DuckDB] Deleting WAL file that may be corrupted: {WalPath}", walPath);
                    try
                    {
                        File.Delete(walPath);
                        _logger.LogInformation("[DuckDB] WAL file deleted successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DuckDB] Failed to delete WAL file");
                    }
                }
            }

            // Reinitialize connections
            _logger.LogDebug("[DuckDB] Reinitializing connections...");
            _schemaInitialized = false;
            InitializeConnections();

            // Re-initialize schema
            _logger.LogDebug("[DuckDB] Re-initializing schema...");
            EnsureSchemaInternal();

            _databaseInvalidated = false;
            _consecutiveFailures = 0;
            RecoveryOccurred = true;
            _logger.LogInformation("[DuckDB] Database recovery completed successfully in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            _logger.LogWarning("[DuckDB] Data may have been lost during recovery. A full reindex is recommended.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DuckDB] Database recovery failed after {ElapsedMs}ms", sw.ElapsedMilliseconds);

            // If recovery fails and we're file-based, consider deleting the database
            if (!_isInMemory && _path is not null && _consecutiveFailures >= MaxConsecutiveFailuresBeforeRecovery)
            {
                _logger.LogWarning("[DuckDB] Multiple recovery attempts failed. Database may need to be deleted manually: {Path}", _path);
            }

            throw new InvalidOperationException($"Database recovery failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Detects if an exception indicates a fatal database error that invalidates the database.
    /// </summary>
    private static bool IsFatalDatabaseError(DuckDBException ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("database has been invalidated", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("FATAL Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("previous fatal error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects if an exception indicates a write-write conflict.
    /// </summary>
    private static bool IsWriteConflictError(DuckDBException ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("write-write conflict", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("TransactionContext Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Handles a fatal database error by marking the database as invalidated.
    /// </summary>
    private void HandleFatalError(DuckDBException ex, string operation)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        _databaseInvalidated = true;

        _logger.LogError(ex,
            "[DuckDB] FATAL ERROR during {Operation} (consecutive failures: {Failures}). " +
            "Database has been invalidated and will attempt recovery on next operation. " +
            "Error: {ErrorMessage}",
            operation, failures, ex.Message);

        // Log additional diagnostic information
        if (!_isInMemory && _path is not null)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var fileInfo = new FileInfo(_path);
                    _logger.LogDebug("[DuckDB] Database file state: {Size:N0} bytes, modified {Modified}",
                        fileInfo.Length, fileInfo.LastWriteTimeUtc);
                }

                var walPath = _path + ".wal";
                if (File.Exists(walPath))
                {
                    var walInfo = new FileInfo(walPath);
                    _logger.LogDebug("[DuckDB] WAL file state: {Size:N0} bytes, modified {Modified}",
                        walInfo.Length, walInfo.LastWriteTimeUtc);
                }
            }
            catch (Exception diagEx)
            {
                _logger.LogDebug(diagEx, "[DuckDB] Failed to gather diagnostic file info");
            }
        }
    }

    /// <summary>
    /// Handles a write-write conflict error.
    /// </summary>
    private void HandleWriteConflict(DuckDBException ex, string operation)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);

        // Extract the conflicting key if present
        var message = ex.Message ?? "";
        var keyMatch = System.Text.RegularExpressions.Regex.Match(message, @"conflict on key: ""([^""]+)""");
        var conflictingKey = keyMatch.Success ? keyMatch.Groups[1].Value : "unknown";

        _logger.LogError(ex,
            "[DuckDB] WRITE-WRITE CONFLICT during {Operation} (consecutive failures: {Failures}). " +
            "Conflicting key: {ConflictingKey}. " +
            "This usually indicates concurrent modifications to the same record. " +
            "The database will be marked as invalidated for recovery.",
            operation, failures, conflictingKey);

        // Write-write conflicts can leave the database in an invalid state
        if (message.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
        {
            _databaseInvalidated = true;
            _logger.LogWarning("[DuckDB] Database invalidated due to fatal write conflict. Will attempt recovery on next operation.");
        }
    }

    private void EnsureSchema()
    {
        if (_schemaInitialized) return;

        _lock.Wait();
        try
        {
            EnsureSchemaInternal();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureSchemaInternal()
    {
        if (_schemaInitialized) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("[DuckDB] Initializing schema...");

        try
        {
            if (!_isInMemory)
            {
                _writer.Execute("SET wal_autocheckpoint = '256MB';");
                _logger.LogDebug("[DuckDB] WAL autocheckpoint set to 256MB");
            }

            _writer.Execute("CREATE TABLE IF NOT EXISTS repo_metadata(key TEXT PRIMARY KEY, value TEXT, updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP);");

            _logger.LogDebug("[DuckDB] Registering UDFs on writer connection...");
            RepositoryUserDefinedFunctions.RegisterAll(_writer, _embeddingProvider);

            var schemaScripts = new[]
            {
                "Tables/artifact.sql",
                "Tables/node.sql",
                "Tables/span.sql",
                "Tables/edge.sql",
                "Macros/entities_by_uri.sql",
                "Macros/json_extract_string_array.sql",
                "Tables/annotation.sql",
                "Views/annotations.sql",
                "Macros/annotations_for.sql",
                "Macros/annotations_all.sql",
                "Macros/glob_match.sql",
                "Tables/document_embedding.sql",
                "Views/repo_index.sql",
                "Macros/snippet.sql",
                "Macros/node_primary_fragment.sql",
                "Macros/xray_documents.sql",
                "Macros/xray_items.sql",
                "Macros/xray_lines.sql",
                "Tables/document_search.sql",
                "Macros/search.sql",
                "Macros/hybrid_search.sql",
                "Tables/file_system_mount.sql"
            };

            foreach (var script in schemaScripts)
            {
                ExecuteSqlResource(script);
            }
            _logger.LogDebug("[DuckDB] Core schema scripts applied ({Count} scripts)", schemaScripts.Length);

            foreach (var script in _formatSchemaScripts)
            {
                try
                {
                    _writer.Execute(script.Sql);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DuckDB] Failed to apply format schema {Id}", script.Identifier);
                }
            }

            if (_formatSchemaScripts.Count > 0)
                _logger.LogDebug("[DuckDB] Format schema scripts applied ({Count} scripts)", _formatSchemaScripts.Count);

            if (!_isInMemory)
            {
                _logger.LogDebug("[DuckDB] Registering UDFs on reader connection...");
                RepositoryUserDefinedFunctions.RegisterAll(_reader, _embeddingProvider);
            }

            _schemaInitialized = true;
            _logger.LogInformation("[DuckDB] Schema initialization completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DuckDB] Schema initialization failed after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            throw;
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

    /// <summary>
    /// Gets whether the database is currently in an invalidated state.
    /// </summary>
    public bool IsInvalidated => _databaseInvalidated;

    /// <summary>
    /// Gets the number of consecutive failures since the last successful operation.
    /// </summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Forces a database health check and recovery attempt if needed.
    /// </summary>
    public void ForceHealthCheck()
    {
        _lock.Wait();
        try
        {
            _logger.LogDebug("[DuckDB] Performing forced health check...");

            try
            {
                // Try a simple query to check if the database is healthy
                using var cmd = _writer.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.ExecuteScalar();
                _logger.LogDebug("[DuckDB] Health check passed");
            }
            catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
            {
                _logger.LogWarning(ex, "[DuckDB] Health check failed, database is in fatal state");
                _databaseInvalidated = true;
                AttemptRecovery();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogDebug("[DuckDB] Disposing data store...");

        _lock.Wait();
        try
        {
            if (!_isInMemory)
            {
                try { _reader?.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing reader connection"); }
            }

            try { _writer?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing writer connection"); }

            _logger.LogDebug("[DuckDB] Data store disposed");
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
