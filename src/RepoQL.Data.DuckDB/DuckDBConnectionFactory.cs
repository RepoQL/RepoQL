using DuckDB.NET.Data;
using System.IO;

namespace RepoQL.Data.DuckDB;

public sealed class DuckDBConnectionFactory(string connectionString) : IDuckDBConnectionFactory
{
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public DuckDBConnection CreateConnection()
    {
        var conn = new DuckDBConnection(_connectionString);
        conn.Open();
        ConfigureMemoryLimit(conn);
        return conn;
    }

    private static void ConfigureMemoryLimit(DuckDBConnection connection)
    {
        var limit = Environment.GetEnvironmentVariable("DUCKDB_MEMORY_LIMIT") ?? "2GB";
        // Use SET because PRAGMA memory_limit may not be available in all builds.
        ExecuteSetting(connection, $"SET memory_limit='{limit}';");
        // Disable object cache to reduce native heap pressure.
        ExecuteSetting(connection, "SET enable_object_cache=false;");
        // Disable insertion order preservation to lower memory usage.
        ExecuteSetting(connection, "SET preserve_insertion_order=false;");
        // Reduce worker count for lower memory/CPU overhead (few concurrent agents).
        var threads = Environment.GetEnvironmentVariable("DUCKDB_THREADS") ?? "1";
        ExecuteSetting(connection, $"SET threads={threads};");
        // Ensure spills go to a deterministic temp directory (relative to working dir).
        var tempDir = Environment.GetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY") ?? ".repoql/index.duckdb.tmp";
        var tempDirPath = Path.GetFullPath(tempDir);
        Directory.CreateDirectory(tempDirPath);
        // DuckDB expects forward slashes; backslashes can be parsed as escapes.
        var tempDirDuck = tempDirPath.Replace("\\", "/");
        ExecuteSetting(connection, $"SET temp_directory='{tempDirDuck}';");
    }

    private static void ExecuteSetting(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
