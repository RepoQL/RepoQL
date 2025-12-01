using DuckDB.NET.Data;

namespace RepoQL.Data.DuckDB;

public sealed class DuckDBConnectionFactory(string connectionString) : IDuckDBConnectionFactory
{
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public DuckDBConnection CreateConnection()
    {
        var conn = new DuckDBConnection(_connectionString);
        conn.Open();
        // Extract database path from connection string (format: "Data Source=path")
        var dbPath = ExtractDataSource(_connectionString);
        DuckDbConnectionConfiguration.Apply(conn, dbPath);
        return conn;
    }

    private static string? ExtractDataSource(string connectionString)
    {
        const string prefix = "Data Source=";
        var idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = connectionString.IndexOf(';', start);
        return end < 0 ? connectionString[start..] : connectionString[start..end];
    }
}
