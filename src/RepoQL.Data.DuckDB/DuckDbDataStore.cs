using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
    private static readonly ActivitySource ActivitySource = new("RepoQL.DuckDB");

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
    private readonly DuckDbStartupOptions _startupOptions;
    private static readonly AsyncLocal<IServiceScope?> _currentScope = new();
    [ThreadStatic]
    private static bool _inQueryContext;
    private bool _schemaInitialized;
    private bool _disposed;
    private bool _databaseInvalidated;
    private bool _databaseFileExisted;
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailuresBeforeRecovery = 3;
    private const int SchemaVersion = 1;

    /// <summary>
    /// Indicates that a database recovery occurred and data may have been lost.
    /// Check this flag on startup or after operations to determine if reindex is needed.
    /// </summary>
    public bool RecoveryOccurred { get; private set; }

    internal ILogger Logger => _logger;

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
    /// Used by UDFs to access services like ExploreOrchestrator.
    /// </summary>
    public static T? GetService<T>() where T : class
        => _currentScope.Value?.ServiceProvider.GetService<T>();

    /// <summary>
    /// Purpose: Force schema initialization for startup validation.
    /// Complexity: Exposes a single entry point without leaking internal locking.
    /// </summary>
    public void InitializeSchema()
        => EnsureSchema();

    public DuckDbDataStore(
        string? path = null,
        IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null,
        ILogger<DuckDbDataStore>? logger = null,
        IServiceProvider? serviceProvider = null,
        DuckDbStartupOptions? startupOptions = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<DuckDbDataStore>.Instance;
        _embeddingProvider = embeddingProvider;
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? [];
        _isInMemory = path is null or ":memory:";
        _databaseFileExisted = !_isInMemory && !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        _serviceProvider = serviceProvider;
        // Always create UdfRegistry - it handles missing serviceProvider gracefully
        _udfRegistry = new UdfRegistry(serviceProvider);
        _startupOptions = startupOptions ?? DuckDbStartupOptionsBuilder.Build(path);

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
            _databaseFileExisted = File.Exists(_path);
            if (_databaseFileExisted)
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
    /// Settings are dynamically calculated based on available hardware, with
    /// environment variable overrides for custom configurations.
    /// </summary>
    /// <remarks>
    /// Environment variables:
    /// <list type="bullet">
    ///   <item><c>DUCKDB_MEMORY_LIMIT</c> - Max memory (default: 60% of RAM, capped at 16GB)</item>
    ///   <item><c>DUCKDB_THREADS</c> - Worker threads (default: physical cores estimate, capped at 8)</item>
    ///   <item><c>DUCKDB_TEMP_DIRECTORY</c> - Spill directory (default: next to database file)</item>
    /// </list>
    /// </remarks>
    private static int _configApplyCount;
    private void ApplyConnectionConfiguration(DuckDBConnection connection)
    {
        var count = Interlocked.Increment(ref _configApplyCount);
        var limit = _startupOptions.MemoryLimit;
        var threads = _startupOptions.Threads;

        ExecSetting(connection, $"SET memory_limit='{limit}';");

        // Enable object cache for single-reader scenarios (caches parsed expressions, schema metadata)
        // Safe since we have few concurrent requests
        ExecSetting(connection, "SET enable_object_cache=true;");

        // Disable insertion order preservation to reduce memory overhead
        ExecSetting(connection, "SET preserve_insertion_order=false;");

        // Use multiple threads for parallel query execution
        // With few concurrent requests, each query can utilize more cores
        ExecSetting(connection, $"SET threads={threads};");

        // Return freed memory to OS more aggressively (default 128MB holds too long)
        ExecSetting(connection, "SET allocator_flush_threshold='64MB';");

        // Ensure spills go to a deterministic temp directory (relative to database, not CWD)
        var tempDirPath = Path.GetFullPath(_startupOptions.TempDirectory).Replace('\\', '/');
        Directory.CreateDirectory(tempDirPath);
        ExecSetting(connection, $"SET temp_directory='{tempDirPath}';");

        // Log settings on first apply with hardware detection details
        if (count == 1)
        {
            var totalMemoryMb = DuckDbDefaults.GetTotalAvailableMemoryMb();
            _logger.LogInformation(
                "[DuckDB] Hardware detected: {LogicalCores} logical cores, {TotalMemoryMb}MB total RAM",
                Environment.ProcessorCount, totalMemoryMb);
            _logger.LogInformation(
                "[DuckDB] Configuration applied: memory_limit={Limit}, threads={Threads}, object_cache=true, flush_threshold=64MB, temp_dir={TempDir}",
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
        if (_inQueryContext)
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
            _inQueryContext = true;
            try
            {
                return ExecuteRead(sql, map);
            }
            finally
            {
                _currentScope.Value = previousScope;
                _inQueryContext = false;
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
        using var activity = ActivitySource.StartActivity("duckdb.query", ActivityKind.Client);
        activity?.SetTag("db.system", "duckdb");
        activity?.SetTag("db.statement", sql.Length > 200 ? sql[..200] + "..." : sql);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));

        activity?.SetTag("db.row_count", results.Count);
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
        using var activity = ActivitySource.StartActivity("duckdb.query.reentrant", ActivityKind.Client);
        activity?.SetTag("db.system", "duckdb");
        activity?.SetTag("db.statement", sql.Length > 200 ? sql[..200] + "..." : sql);

        var conn = GetReentrantConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));

        activity?.SetTag("db.row_count", results.Count);
        return results;
    }

    private T? ExecuteReentrantScalar<T>(string sql)
    {
        using var activity = ActivitySource.StartActivity("duckdb.scalar.reentrant", ActivityKind.Client);
        activity?.SetTag("db.system", "duckdb");
        activity?.SetTag("db.statement", sql.Length > 200 ? sql[..200] + "..." : sql);

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
        if (_inQueryContext)
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
            _inQueryContext = true;
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
                _inQueryContext = false;
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
        if (_inQueryContext)
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
            _inQueryContext = true;
            try
            {
                return ExecuteScalar<T>(sql);
            }
            finally
            {
                _currentScope.Value = previousScope;
                _inQueryContext = false;
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
            tx = BeginTransactionWithRecovery("WriteTransaction");
            work(_connection, tx);
            tx.Commit();
            tx = null; // Mark as committed so we don't rollback
            Interlocked.Exchange(ref _consecutiveFailures, 0); // Reset on success
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            TryRollback(tx);
            tx = null;
            HandleFatalError(ex, "WriteTransaction");
            throw;
        }
        catch (DuckDBException ex) when (IsWriteConflictError(ex))
        {
            TryRollback(tx);
            tx = null;
            HandleWriteConflict(ex, "WriteTransaction");
            throw;
        }
        catch (Exception)
        {
            TryRollback(tx);
            tx = null;
            throw;
        }
        finally
        {
            TryDispose(tx);
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
        finally
        {
            TryDispose(tx);
        }
    }

    private void TryDispose(DuckDBTransaction? tx)
    {
        if (tx is null) return;
        try
        {
            tx.Dispose();
        }
        catch (DuckDBException ex) when (ex.ErrorType == DuckDBErrorType.Transaction && ex.ErrorCode == 10)
        {
            // "No transaction active" — expected when DuckDB already auto-rolled-back.
            // Dispose internally calls Rollback which throws this; safe to ignore.
        }
        catch (Exception disposeEx)
        {
            _logger.LogError(disposeEx, "[DuckDB] Transaction dispose failed - connection may be corrupted, marking for recovery");
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
            tx = BeginTransactionWithRecovery("WriteTransaction<T>");
            var result = work(_connection, tx);
            tx.Commit();
            tx = null; // Mark as committed so we don't rollback
            Interlocked.Exchange(ref _consecutiveFailures, 0); // Reset on success
            return result;
        }
        catch (DuckDBException ex) when (IsFatalDatabaseError(ex))
        {
            TryRollback(tx);
            tx = null;
            HandleFatalError(ex, "WriteTransaction<T>");
            throw;
        }
        catch (DuckDBException ex) when (IsWriteConflictError(ex))
        {
            TryRollback(tx);
            tx = null;
            HandleWriteConflict(ex, "WriteTransaction<T>");
            throw;
        }
        catch (Exception)
        {
            TryRollback(tx);
            tx = null;
            throw;
        }
        finally
        {
            TryDispose(tx);
            _lock.Release();
        }
    }

    private DuckDBTransaction BeginTransactionWithRecovery(string operation)
    {
        try
        {
            return _connection.BeginTransaction();
        }
        catch (InvalidOperationException ex) when (IsAlreadyInTransactionError(ex))
        {
            _logger.LogWarning(ex,
                "[DuckDB] Connection reported an active transaction during {Operation}. Attempting stale transaction recovery.",
                operation);

            TryClearStaleTransactionState();

            try
            {
                return _connection.BeginTransaction();
            }
            catch (InvalidOperationException retryEx) when (IsAlreadyInTransactionError(retryEx))
            {
                _logger.LogError(retryEx,
                    "[DuckDB] Stale transaction recovery failed during {Operation}. Marking database invalidated and attempting full recovery.",
                    operation);

                _databaseInvalidated = true;
                AttemptRecovery();
                return _connection.BeginTransaction();
            }
        }
    }

    private void TryClearStaleTransactionState()
    {
        var activeTransaction = GetActiveTransactionReference();
        if (activeTransaction is not null)
        {
            TryDispose(activeTransaction);
        }

        try
        {
            _connection.Execute("ROLLBACK;");
        }
        catch (DuckDBException ex) when (ex.ErrorType == DuckDBErrorType.Transaction && ex.ErrorCode == 10)
        {
            // No active transaction in native engine.
        }
        catch (Exception rollbackEx)
        {
            _logger.LogDebug(rollbackEx, "[DuckDB] ROLLBACK during stale transaction recovery failed");
        }
    }

    private DuckDBTransaction? GetActiveTransactionReference()
    {
        var field = typeof(DuckDBConnection).GetField("_activeTransaction", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(_connection) as DuckDBTransaction;
    }

    private static bool IsAlreadyInTransactionError(InvalidOperationException ex)
        => ex.Message.Contains("Already in a transaction", StringComparison.OrdinalIgnoreCase);

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
            _logger.LogDebug("[DuckDB] Closing existing connection...");
            CloseConnections();

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

    private void CloseConnections()
    {
        try { _connection?.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error closing connection"); }
        try { _connection?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing connection"); }

        lock (_reentrantConnectionLock)
        {
            try { _reentrantConnection?.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error closing reentrant connection"); }
            try { _reentrantConnection?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[DuckDB] Error disposing reentrant connection"); }
            _reentrantConnection = null;
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var walPath = path + ".wal";
        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }
    }

    /// <summary>
    /// Purpose: Recreate the database file when schema or corruption recovery requires a rebuild.
    /// Complexity: Coordinates connection teardown, file deletion, and schema reinitialization.
    /// </summary>
    public void RecreateDatabase()
    {
        if (_isInMemory || _path is null)
            throw new InvalidOperationException("Cannot recreate an in-memory database.");

        _lock.Wait();
        try
        {
            _logger.LogWarning("[DuckDB] Recreating database at {Path}", _path);

            CloseConnections();

            DeleteDatabaseFiles(_path);

            _schemaInitialized = false;
            _databaseInvalidated = false;
            _consecutiveFailures = 0;
            _databaseFileExisted = false;

            InitializeConnections();
            EnsureSchemaInternal();

            RecoveryOccurred = true;
        }
        finally
        {
            _lock.Release();
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

    private void EnsureSchemaCompatibility()
    {
        if (_isInMemory || !_databaseFileExisted)
            return;

        var storedVersion = TryReadMetadataValue(MetadataKeySchemaVersion);
        var expectedVersion = SchemaVersion.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(storedVersion))
        {
            throw new DuckDbSchemaMismatchException("Schema version metadata missing.");
        }

        if (!string.Equals(storedVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new DuckDbSchemaMismatchException(
                $"Schema version {storedVersion} does not match expected {expectedVersion}.");
        }
    }

    private void EnsureSchemaInternal()
    {
        if (_schemaInitialized) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("[DuckDB] Initializing schema...");

        try
        {
            EnsureSchemaCompatibility();

            if (!_isInMemory)
            {
                _connection.Execute("SET wal_autocheckpoint = '16MB';");
                _logger.LogDebug("[DuckDB] WAL autocheckpoint set to 16MB");
            }

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

            // Load VSS extension for HNSW vector similarity search
            // This must happen AFTER UDF registration to avoid conflicts with DuckDB's internal function registration
            try
            {
                _connection.Execute("INSTALL vss;");
                _connection.Execute("LOAD vss;");
                _logger.LogDebug("[DuckDB] VSS extension loaded for HNSW search");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DuckDB] VSS extension not available - falling back to linear search");
            }

            var schemaScripts = new[]
            {
                "Tables/metadata.sql",
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
                "Macros/matches_glob.sql",
                "Macros/glob_files.sql",
                // git_status must come before Views/files.sql which uses it
                "Macros/git_status.sql",
                "Tables/document_embedding.sql",
                "Tables/vss_indexes.sql",
                "Views/repo_index.sql",
                "Views/files.sql",
                "Views/types.sql",
                "Views/functions.sql",
                "Macros/snippet.sql",
                "Macros/grep_matches.sql",
                "Macros/regex_matches.sql",
                "Macros/node_primary_fragment.sql",
                "Macros/search_helpers.sql",
                "Macros/search_lexical.sql",
                "Macros/search_semantic.sql",
                "Macros/search_debug.sql",
                "Macros/search.sql",
                "Macros/hybrid_search.sql",
                "Macros/search_symbol.sql",
                "Tables/file_system_mount.sql",
                "Views/filesystems.sql",
                "Macros/explore.sql",
                "Macros/explore_structured.sql",
                "Macros/parse.sql",
                // Git history tables, views, and macros
                "Tables/git_commit.sql",
                "Tables/git_file_change.sql",
                "Views/git_hotspots.sql",
                "Views/git_recent.sql",
                "Macros/git_file_history.sql",
                "Macros/git_blame.sql",
                "Macros/git_diff.sql",
                "Macros/git_patches.sql",
                "Macros/changes_related_to.sql",
                "Macros/similar.sql"
            };

            foreach (var script in schemaScripts)
            {
                ExecuteSqlResource(script);
            }
            _logger.LogDebug("[DuckDB] Core schema scripts applied ({Count} scripts)", schemaScripts.Length);

            EnsureSchemaVersion();

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

            // Check if assembly version changed - if so, invalidate embedded documentation cache
            CheckAndUpdateVersion();

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

    private const string MetadataKeySchemaVersion = "schema_version";
    private const string MetadataKeyAssemblyVersion = "assembly_version";
    private const string EmbeddedDocsScheme = "help://";

    private void EnsureSchemaVersion()
    {
        var version = SchemaVersion.ToString(CultureInfo.InvariantCulture);
        UpsertMetadataValue(MetadataKeySchemaVersion, version);
    }

    /// <summary>
    /// Read a metadata value by key. Returns null if the key or table doesn't exist.
    /// </summary>
    public string? ReadMetadataValue(string key) => TryReadMetadataValue(key);

    private string? TryReadMetadataValue(string key)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM metadata WHERE key = $1";
            cmd.Parameters.Add(new DuckDBParameter { Value = key });
            return cmd.ExecuteScalar() as string;
        }
        catch (DuckDBException ex) when (IsMissingMetadataTable(ex))
        {
            return null;
        }
    }

    private static bool IsMissingMetadataTable(DuckDBException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("metadata", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private void UpsertMetadataValue(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO metadata (key, value, updated_at) VALUES ($1, $2, now())
            ON CONFLICT (key) DO UPDATE SET value = $2, updated_at = now()";
        cmd.Parameters.Add(new DuckDBParameter { Value = key });
        cmd.Parameters.Add(new DuckDBParameter { Value = value });
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Checks if the assembly version changed since last run. If so, deletes all
    /// embedded documentation artifacts so they get re-indexed with fresh content.
    /// </summary>
    private void CheckAndUpdateVersion()
    {
        var currentVersion = typeof(DuckDbDataStore).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DuckDbDataStore).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        try
        {
            var storedVersion = TryReadMetadataValue(MetadataKeyAssemblyVersion);

            if (storedVersion == currentVersion)
            {
                _logger.LogDebug("[DuckDB] Version unchanged ({Version}), embedded docs cache valid", currentVersion);
                return;
            }

            if (storedVersion is not null)
            {
                _logger.LogInformation("[DuckDB] Version changed from {OldVersion} to {NewVersion}, invalidating embedded docs cache",
                    storedVersion, currentVersion);

                // Delete all artifacts with help:// URIs
                var deleteCount = DeleteArtifactsByUriPrefix(EmbeddedDocsScheme);
                _logger.LogInformation("[DuckDB] Deleted {Count} embedded documentation artifacts for re-indexing", deleteCount);
            }
            else
            {
                _logger.LogDebug("[DuckDB] First run, setting version to {Version}", currentVersion);
            }

            UpsertMetadataValue(MetadataKeyAssemblyVersion, currentVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DuckDB] Version check failed, embedded docs may be stale");
        }
    }

    /// <summary>
    /// Deletes all documents (nodes and their artifacts) whose URI starts with the given prefix.
    /// Used to invalidate embedded documentation cache on version change.
    /// </summary>
    private int DeleteArtifactsByUriPrefix(string uriPrefix)
    {
        // Get all document node IDs and their artifact IDs matching the URI prefix
        // URI is stored on node table; artifacts are content-addressed blobs referenced by nodes
        using var selectCmd = _connection.CreateCommand();
        selectCmd.CommandText = @"
            SELECT id, artifact_id
            FROM node
            WHERE uri LIKE $1 || '%' AND kind = 'document'";
        selectCmd.Parameters.Add(new DuckDBParameter { Value = uriPrefix });

        var nodesToDelete = new List<(Guid NodeId, Guid? ArtifactId)>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var nodeId = reader.GetGuid(0);
                var artifactId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
                nodesToDelete.Add((nodeId, artifactId));
            }
        }

        if (nodesToDelete.Count == 0)
            return 0;

        // Delete in batches to avoid parameter limits
        foreach (var (nodeId, artifactId) in nodesToDelete)
        {
            // Delete embeddings (doc_id references the document node)
            using var deleteEmbedCmd = _connection.CreateCommand();
            deleteEmbedCmd.CommandText = "DELETE FROM document_embedding WHERE doc_id = $1";
            deleteEmbedCmd.Parameters.Add(new DuckDBParameter { Value = nodeId });
            deleteEmbedCmd.ExecuteNonQuery();

            // Delete annotations (scope_document_id references the document node)
            using var deleteAnnotCmd = _connection.CreateCommand();
            deleteAnnotCmd.CommandText = "DELETE FROM annotation WHERE scope_document_id = $1";
            deleteAnnotCmd.Parameters.Add(new DuckDBParameter { Value = nodeId });
            deleteAnnotCmd.ExecuteNonQuery();

            // Delete spans (document_id references the document node)
            using var deleteSpanCmd = _connection.CreateCommand();
            deleteSpanCmd.CommandText = "DELETE FROM span WHERE document_id = $1";
            deleteSpanCmd.Parameters.Add(new DuckDBParameter { Value = nodeId });
            deleteSpanCmd.ExecuteNonQuery();

            // Delete edges involving any nodes from this document
            using var deleteEdgeCmd = _connection.CreateCommand();
            deleteEdgeCmd.CommandText = @"
                DELETE FROM edge
                WHERE source_node_id IN (SELECT id FROM node WHERE artifact_id = $1)
                   OR destination_node_id IN (SELECT id FROM node WHERE artifact_id = $1)";
            deleteEdgeCmd.Parameters.Add(new DuckDBParameter { Value = artifactId ?? Guid.Empty });
            deleteEdgeCmd.ExecuteNonQuery();

            // Delete all nodes referencing this artifact (includes child nodes)
            if (artifactId.HasValue)
            {
                using var deleteNodeCmd = _connection.CreateCommand();
                deleteNodeCmd.CommandText = "DELETE FROM node WHERE artifact_id = $1";
                deleteNodeCmd.Parameters.Add(new DuckDBParameter { Value = artifactId.Value });
                deleteNodeCmd.ExecuteNonQuery();

                // Delete the artifact itself
                using var deleteArtCmd = _connection.CreateCommand();
                deleteArtCmd.CommandText = "DELETE FROM artifact WHERE id = $1";
                deleteArtCmd.Parameters.Add(new DuckDBParameter { Value = artifactId.Value });
                deleteArtCmd.ExecuteNonQuery();
            }
        }

        return nodesToDelete.Count;
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
