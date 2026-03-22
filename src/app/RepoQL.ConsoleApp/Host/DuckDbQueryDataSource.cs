using RepoQL.Data.DuckDB;
using RepoQL.Query;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Adapt DuckDbDataStore to the IQueryDataSource interface consumed by QueryEngine.
/// Complexity: Thin wrapper — delegates directly to <see cref="DuckDbDataStore.Query"/>.
/// </summary>
internal sealed class DuckDbQueryDataSource(DuckDbDataStore db) : IQueryDataSource
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, CancellationToken cancel)
        => db.Query(sql, cancel);
}
