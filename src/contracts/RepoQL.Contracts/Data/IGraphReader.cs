using System.Data;

namespace RepoQL.Contracts.Data;

/// <summary>
/// Read-only SQL access to the graph for cross-document analyzers.
/// Implemented by DuckDbDataStore, injected into multi-file analyzers
/// so format projects don't need a direct DuckDB dependency.
/// </summary>
public interface IGraphReader
{
    IReadOnlyList<T> Read<T>(string sql, Func<IDataRecord, T> map, CancellationToken cancel = default);
}
