using System.Data;

namespace RepoQL.Contracts.Data;

/// <summary>
/// Purpose: Read-only SQL access to the graph for cross-document analysis.
/// Complexity: Thin abstraction over the data store's query surface.
/// Format loaders and analyzers use this to read graph state without
/// depending on the concrete data layer.
/// </summary>
public interface IGraphQueryService
{
    /// <summary>
    /// Execute a SQL query and map results.
    /// </summary>
    IReadOnlyList<T> Read<T>(string sql, Func<IDataRecord, T> map, CancellationToken cancel = default);
}
