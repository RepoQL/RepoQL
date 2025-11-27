using DuckDB.NET.Data;
using RepoQL.Contracts;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Factory for creating <see cref="DuckDbGraphStore"/> instances with proper dependency injection.
/// </summary>
public interface IDuckDbGraphStoreFactory
{
    /// <summary>
    /// Creates a new graph store using the provided connection.
    /// </summary>
    DuckDbGraphStore Create(DuckDBConnection connection, IEnumerable<FormatSqlScript>? formatSchemaScripts = null);
}
