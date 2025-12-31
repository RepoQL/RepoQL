using System.Data;
using DuckDB.NET.Data;
using DuckDB.NET.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Thread-safe DuckDB data store with lazy schema initialization.
/// All database access should go through Read/WriteTransaction methods.
/// Includes automatic detection and recovery from database corruption.
/// </summary>
public sealed class DuckDbDataStore : IDisposable
{
    private readonly string? _path;
    private DuckDBConnection _connection = null!; // Initialized in InitializeConnections
    private DuckDBConnection? _reentrantConnection; // Secondary read-only connection for UDF callbacks
    private readonly object _reentrantConnectionLock = new();
    private readonly SemaphoreSlim _lock = new(1, 1); // DuckDB connections aren't thread-safe for concurrent commands
    private readonly ILogger _logger;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IReadOnlyList<FormatSqlScript> _formatSchemaScripts;
    private readonly bool _isInMemory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly UdfRegistry _udfRegistry;
    private static readonly AsyncLocal<IServiceScope?> _currentScope = new();
    private static readonly AsyncLocal<bool> _inQueryContext = new();
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

    /// <summary>
    /// Executes an action within a DI scope, making services available to UDFs via GetService.
    /// </summary>
    public T WithScope<T>(Func<T> action)
    {
        if (_serviceProvider is null)
            return action();

        using var scope = _serviceProvider.CreateScope();
        var previous = _currentScope.Value;
        _currentScope.Value = scope;
        try
        {
            return action();
        }
        finally
        {
            _currentScope.Value = previous;
        }
    }

    /// <summary>
    /// Resolves a service from the current AsyncLocal scope.
    /// Used by UDFs to access services like XrayOrchestrator.
    /// </summary>
    public static T? GetService<T>() where T : class
        => _currentScope.Value?.ServiceProvider.GetService<T>();

    public DuckDbDataStore(
        string? path = null,
        IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null,
        ILogger<DuckDbDataStore>? logger = null,
        IServiceProvider? serviceProvider = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<DuckDbDataStore>.Instance;
        _embeddingProvider = embeddingProvider;
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? [];
        _isInMemory = path is null or ":memory:";
        _serviceProvider = serviceProvider;
        // Always create UdfRegistry - it handles missing serviceProvider gracefully
        _udfRegistry = new UdfRegistry(serviceProvider);

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
            _connection = new DuckDBConnection("Data Source=:memory:");
            _connection.Open();
            ApplyConnectionConfiguration(_connection);
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
                _connection = new DuckDBConnection($"Data Source={_path};ACCESS_MODE=READ_WRITE");
                _connection.Open();
                ApplyConnectionConfiguration(_connection);
                _logger.LogDebug("[DuckDB] Connection opened");

                // Check for suspiciously large WAL that may indicate corruption from prior crash
                ValidateWalState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DuckDB] Failed to open database connection for {Path}", _path);
                throw;
            }
        }

        _logger.LogDebug("[DuckDB] Connection initialized in {ElapsedMs}ms", sw.ElapsedMilliseconds);
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

        // Detect reentrant calls (e.g., from UDFs that query the database)
        // If already in a query context, use secondary connection to avoid deadlock
        if (_inQueryContext.Value)
        {
            return ExecuteReentrantRead(sql, map);
        }

        _lock.Wait();
        try
        {
            // Set up DI scope for UDFs that need service resolution
            using var scope = _serviceProvider?.CreateScope();
            var previousScope = _currentScope.Value;
            _currentScope.Value = scope;
            _inQueryContext.Value = true;
            try
            {
                return ExecuteRead(sql, map);
            }
            finally
            {
                _currentScope.Value = previousScope;
                _inQueryContext.Value = false;
            }
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            HandleFatalError(ex, "Read");
            throw;
        }
        catch (DuckDBException ex) when (IsWriteConflictError(ex))
        {
            // Write conflicts can occur during reads when DuckDB applies WAL entries
            HandleWriteConflict(ex, "Read");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private IReadOnlyList<T> ExecuteRead<T>(string sql, Func<IDataRecord, T> map)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));
        return results;
    }

    /// <summary>
    /// Gets or creates a secondary read-only connection for reentrant queries (e.g., from UDFs).
    /// This connection is separate from the primary connection to avoid deadlocks.
    /// </summary>
    private DuckDBConnection GetReentrantConnection()
    {
        if (_reentrantConnection is not null)
            return _reentrantConnection;

        lock (_reentrantConnectionLock)
        {
            if (_reentrantConnection is not null)
                return _reentrantConnection;

            _logger.LogDebug("[DuckDB] Creating secondary read-only connection for reentrant queries");

            if (_isInMemory)
            {
                // For in-memory databases, we can't create a second connection to the same database
                // Fall back to the primary connection (caller must handle potential issues)
                _logger.LogWarning("[DuckDB] In-memory database doesn't support reentrant connection; using primary");
                return _connection;
            }

            var connStr = $"Data Source={_path}";
            _reentrantConnection = new DuckDBConnection(connStr);
            _reentrantConnection.Open();

            return _reentrantConnection;
        }
    }

    private IReadOnlyList<T> ExecuteReentrantRead<T>(string sql, Func<IDataRecord, T> map)
    {
        var conn = GetReentrantConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));
        return results;
    }

    private T? ExecuteReentrantScalar<T>(string sql)
    {
        var conn = GetReentrantConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return default;
        if (result is T typed) return typed;
        return (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>
    /// Execute a query from an untrusted source (e.g., MCP client) in a read-only transaction.
    /// DuckDB enforces at the engine level that no writes can occur, regardless of SQL content.
    /// </summary>
    public IReadOnlyList<T> ReadUntrusted<T>(string sql, Func<IDataRecord, T> map)
    {
        EnsureSchema();

        if (_databaseInvalidated)
        {
            CheckAndRecoverIfNeeded();
        }

        // Detect reentrant calls - use secondary connection to avoid deadlock
        if (_inQueryContext.Value)
        {
            return ExecuteReentrantRead(sql, map);
        }

        _lock.Wait();
        try
        {
            // Set up DI scope for UDFs that need service resolution
            using var scope = _serviceProvider?.CreateScope();
            var previousScope = _currentScope.Value;
            _currentScope.Value = scope;
            _inQueryContext.Value = true;
            try
            {
                // Start a read-only transaction - DuckDB will reject any write attempts
                _connection.Execute("BEGIN TRANSACTION READ ONLY;");
                try
                {
                    var results = ExecuteRead(sql, map);
                    _connection.Execute("COMMIT;");
                    return results;
                }
                catch
                {
                    try { _connection.Execute("ROLLBACK;"); } catch { /* ignore rollback errors */ }
                    throw;
                }
            }
            finally
            {
                _currentScope.Value = previousScope;
                _inQueryContext.Value = false;
            }
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            HandleFatalError(ex, "ReadUntrusted");
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

        // Detect reentrant calls - use secondary connection to avoid deadlock
        if (_inQueryContext.Value)
        {
            return ExecuteReentrantScalar<T>(sql);
        }

        _lock.Wait();
        try
        {
            // Set up DI scope for UDFs that need service resolution
            using var scope = _serviceProvider?.CreateScope();
            var previousScope = _currentScope.Value;
            _currentScope.Value = scope;
            _inQueryContext.Value = true;
            try
            {
                return ExecuteScalar<T>(sql);
            }
            finally
            {
                _currentScope.Value = previousScope;
                _inQueryContext.Value = false;
            }
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            HandleFatalError(ex, "ReadScalar");
            throw;
        }
        catch (DuckDBException ex) when (IsWriteConflictError(ex))
        {
            HandleWriteConflict(ex, "ReadScalar");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private T? ExecuteScalar<T>(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return default;
        if (result is T typed) return typed;
        return (T)Convert.ChangeType(result, typeof(T));
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
            tx = _connection.BeginTransaction();
            work(_connection, tx);
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
        catch (DuckDBException ex) when (ex.ErrorType == DuckDBErrorType.Transaction && ex.ErrorCode == 10)
        {
            // No transaction active
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
            tx = _connection.BeginTransaction();
            var result = work(_connection, tx);
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
    /// Executes raw SQL statements (for macro registration from external sources).
    /// Use sparingly - prefer WriteTransaction for transactional safety.
    /// </summary>
    public void ExecuteRaw(string sql)
    {
        EnsureSchema();

        if (_databaseInvalidated)
        {
            CheckAndRecoverIfNeeded();
        }

        _lock.Wait();
        try
        {
            _connection.Execute(sql);
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
            // Close existing connection (reader == writer, so only close once)
            _logger.LogDebug("[DuckDB] Closing existing connection...");
            try { _connection?.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error closing connection"); }
            try { _connection?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing connection"); }

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
    /// Validates WAL state on startup and ensures clean database state.
    /// Aggressively checkpoints and verifies WAL is empty to prevent read conflicts.
    /// </summary>
    private void ValidateWalState()
    {
        if (_isInMemory || _path is null) return;

        var walPath = _path + ".wal";
        if (!File.Exists(walPath)) return;

        var walInfo = new FileInfo(walPath);
        _logger.LogDebug("[DuckDB] WAL file exists on startup: {Size:N0} bytes", walInfo.Length);

        // Try to abort any stale transaction from a previous crashed session
        try
        {
            _connection.Execute("ROLLBACK;");
            _logger.LogDebug("[DuckDB] Rolled back stale transaction on startup");
        }
        catch (DuckDBException)
        {
            // No active transaction - this is the expected case
        }

        // Checkpoint to flush WAL to main database file
        try
        {
            _connection.Execute("CHECKPOINT;");
            _logger.LogDebug("[DuckDB] Startup checkpoint completed");
        }
        catch (DuckDBException ex)
        {
            _logger.LogWarning(ex, "[DuckDB] Startup checkpoint failed");
        }

        // After checkpoint, WAL should be minimal (just headers, typically < 4KB)
        // If it's still substantial, the WAL has problematic entries that can't be cleanly applied
        // Delete it to prevent read conflicts from "applying buffered appends"
        walInfo.Refresh();
        if (walInfo.Exists && walInfo.Length > 4096)
        {
            _logger.LogWarning(
                "[DuckDB] WAL still has {Size:N0} bytes after checkpoint. " +
                "Deleting to prevent read conflicts. Some data may be lost.",
                walInfo.Length);

            // Close connection, delete WAL, reinitialize
            try { _connection.Close(); } catch { /* ignore */ }
            try { _connection.Dispose(); } catch { /* ignore */ }

            File.Delete(walPath);

            // Reinitialize connection
            _connection = new DuckDBConnection($"Data Source={_path};ACCESS_MODE=READ_WRITE");
            _connection.Open();
            ApplyConnectionConfiguration(_connection);
            RecoveryOccurred = true;

            _logger.LogInformation("[DuckDB] Reinitialized connection after WAL cleanup");
        }
        else
        {
            _logger.LogInformation("[DuckDB] Startup WAL validation complete");
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
               message.Contains("INTERNAL Error", StringComparison.OrdinalIgnoreCase) ||
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
            "This usually indicates WAL corruption or concurrent access issues. " +
            "The database will be marked as invalidated for recovery.",
            operation, failures, conflictingKey);

        // Write-write conflicts during reads indicate WAL/database corruption
        // INTERNAL errors are DuckDB assertion failures that require recovery
        var isRead = operation.StartsWith("Read", StringComparison.Ordinal);
        var isInternalError = message.Contains("INTERNAL Error", StringComparison.OrdinalIgnoreCase);
        var isFatalError = message.Contains("FATAL", StringComparison.OrdinalIgnoreCase);

        if (isRead || isInternalError || isFatalError)
        {
            _databaseInvalidated = true;
            _logger.LogWarning("[DuckDB] Database invalidated due to {Reason}. Will attempt recovery on next operation.",
                isInternalError ? "internal error" : isRead ? "conflict during read" : "fatal error");
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
                _connection.Execute("SET wal_autocheckpoint = '16MB';");
                _logger.LogDebug("[DuckDB] WAL autocheckpoint set to 16MB");
            }

            _logger.LogDebug("[DuckDB] Registering UDFs on writer connection...");
            RepositoryUserDefinedFunctions.RegisterAll(_connection, _embeddingProvider);

            // Register attribute-based UDFs via the framework
            _logger.LogDebug("[DuckDB] Discovering and registering framework UDFs...");
            _udfRegistry.DiscoverAndRegister(_connection);

            // Execute auto-generated macros for framework UDFs
            var macrosSql = _udfRegistry.GenerateMacrosSql();
            if (!string.IsNullOrWhiteSpace(macrosSql))
            {
                _connection.Execute(macrosSql);
                _logger.LogDebug("[DuckDB] Framework UDF macros applied");
            }

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
                "Views/files.sql",
                "Views/types.sql",
                "Views/functions.sql",
                "Macros/snippet.sql",
                "Macros/node_primary_fragment.sql",
                "Macros/xray_documents.sql",
                "Macros/xray_items.sql",
                "Macros/xray_lines.sql",
                "Macros/search.sql",
                "Macros/hybrid_search.sql",
                "Tables/file_system_mount.sql",
                "Macros/xray.sql",
                "Macros/xray_structured.sql"
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
                    _connection.Execute(script.Sql);
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
                RepositoryUserDefinedFunctions.RegisterAll(_connection, _embeddingProvider);
                // Note: Framework UDFs already registered above, no need to re-register
            }

            _schemaInitialized = true;

            // Checkpoint WAL on startup to flush any uncommitted entries from previous session
            // This prevents write-write conflicts caused by stale WAL state
            if (!_isInMemory)
            {
                try
                {
                    _connection.Execute("CHECKPOINT;");
                    _logger.LogDebug("[DuckDB] Startup checkpoint completed");
                }
                catch (DuckDBException ex)
                {
                    _logger.LogWarning(ex, "[DuckDB] Startup checkpoint failed - may have stale WAL entries");
                }
            }

            // Verify database is healthy after schema initialization
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM node LIMIT 1";
                cmd.ExecuteScalar();
            }
            catch (DuckDBException ex) when (IsFatalDatabaseError(ex) || IsWriteConflictError(ex))
            {
                _logger.LogError(ex, "[DuckDB] Database health check failed on startup");
                _databaseInvalidated = true;
                AttemptRecovery();
            }

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
        _connection.Execute(reader.ReadToEnd());
    }

    /// <summary>
    /// Attempts a WAL checkpoint. Safe to call frequently - will not throw on failure.
    /// Use this at natural boundaries (e.g., after indexing phases) to prevent WAL accumulation.
    /// </summary>
    /// <returns>True if checkpoint succeeded, false otherwise.</returns>
    public bool TryCheckpoint()
    {
        if (_isInMemory) return true;
        if (_databaseInvalidated) return false;

        _lock.Wait();
        try
        {
            _connection.Execute("CHECKPOINT;");
            _logger.LogDebug("[DuckDB] Checkpoint completed");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DuckDB] Checkpoint failed");
            return false;
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
            // Checkpoint WAL before closing to ensure clean state on next startup
            if (!_isInMemory && !_databaseInvalidated)
            {
                try
                {
                    _logger.LogDebug("[DuckDB] Checkpointing WAL before shutdown...");
                    _connection.Execute("CHECKPOINT;");
                    _logger.LogDebug("[DuckDB] WAL checkpoint completed");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DuckDB] Failed to checkpoint WAL on shutdown - next startup may need recovery");
                }
            }

            // Dispose reentrant connection if created
            try { _reentrantConnection?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing reentrant connection"); }

            // Dispose primary connection
            try { _connection?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing connection"); }

            _logger.LogDebug("[DuckDB] Data store disposed");
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
