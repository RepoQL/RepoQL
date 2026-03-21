using System.Data;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Lock-free read interface for use inside UDF callbacks.
/// Complexity: Two methods that bypass DuckDbDataStore's exclusive section,
/// using a secondary read-only connection that is safe during reentrant calls
/// from DuckDB worker threads.
/// </summary>
public interface IReentrantReader
{
    IReadOnlyList<T> Read<T>(string sql, Func<IDataRecord, T> map, CancellationToken cancellationToken = default);
    T? ReadScalar<T>(string sql, CancellationToken cancellationToken = default);
}
