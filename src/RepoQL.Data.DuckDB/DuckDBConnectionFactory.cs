using DuckDB.NET.Data;

namespace RepoQL.Data.DuckDB;

public sealed class DuckDBConnectionFactory(string connectionString) : IDuckDBConnectionFactory
{
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public DuckDBConnection CreateConnection()
    {
        var conn = new DuckDBConnection(_connectionString);
        conn.Open();
        DuckDbConnectionConfiguration.Apply(conn);
        return conn;
    }
}
