using DuckDB.NET.Data;

namespace RepoQL.Data.DuckDB;

public interface IDuckDBConnectionFactory
{
    DuckDBConnection CreateConnection();
}