using DuckDB.NET.Data;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Centralized DuckDB connection configuration for memory, threading, and temp storage settings.
/// Both <see cref="DuckDBConnectionFactory"/> and <see cref="DuckDbGraphStore"/> use this
/// to ensure consistent configuration regardless of how connections are created.
/// </summary>
public static class DuckDbConnectionConfiguration
{
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
    private static int _applyCount;

    public static void Apply(DuckDBConnection connection, string? databasePath = null)
    {
        var count = Interlocked.Increment(ref _applyCount);

        var limit = Environment.GetEnvironmentVariable("DUCKDB_MEMORY_LIMIT") ?? "8GB";
        Exec(connection, $"SET memory_limit='{limit}';");

        // Disable object cache - defaults to 80% of RAM which is far too aggressive
        Exec(connection, "SET enable_object_cache=false;");

        // Disable insertion order preservation to reduce memory overhead
        Exec(connection, "SET preserve_insertion_order=false;");

        var threads = Environment.GetEnvironmentVariable("DUCKDB_THREADS") ?? "1";
        Exec(connection, $"SET threads={threads};");

        // Return freed memory to OS more aggressively (default 128MB holds too long)
        Exec(connection, "SET allocator_flush_threshold='64MB';");

        // Ensure spills go to a deterministic temp directory (relative to database, not CWD)
        var tempDir = Environment.GetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY");
        if (string.IsNullOrEmpty(tempDir) && !string.IsNullOrEmpty(databasePath))
        {
            // Default to temp directory next to the database file
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            tempDir = Path.Combine(dbDir ?? ".", "index.duckdb.tmp");
        }
        tempDir ??= ".repoql/index.duckdb.tmp";
        var tempDirPath = Path.GetFullPath(tempDir).Replace("\\", "/", StringComparison.Ordinal);
        Directory.CreateDirectory(tempDirPath);
        Exec(connection, $"SET temp_directory='{tempDirPath}';");

        // Log settings on first apply
        if (count == 1)
        {
            Console.Error.WriteLine($"[DuckDB] Configuration applied: memory_limit={limit}, threads={threads}, object_cache=false, flush_threshold=64MB, temp_dir={tempDirPath}");
        }
    }

    private static void Exec(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
