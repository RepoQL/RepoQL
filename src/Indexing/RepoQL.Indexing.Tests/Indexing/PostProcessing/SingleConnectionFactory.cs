using DuckDB.NET.Data;
using RepoQL.Data.DuckDB;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal sealed class SingleConnectionFactory(DuckDBConnection connection) : IDuckDBConnectionFactory
{
    private bool _provided;

    public DuckDBConnection CreateConnection()
    {
        if (_provided)
            throw new InvalidOperationException("Connection already provided.");
        _provided = true;
        return connection;
    }
}