namespace RepoQL.Query;

/// <summary>
/// Purpose: Execute SQL queries against the graph with budget-aware result handling.
/// Complexity: Encapsulates parameter substitution, result mapping, and token budget
/// summarization — the pure business logic of the query tool without transport knowledge.
/// </summary>
public interface IQueryEngine
{
    /// <summary>
    /// Execute a SQL query and return transport-agnostic results.
    /// </summary>
    /// <param name="sql">DuckDB SQL to execute.</param>
    /// <param name="tokenBudget">Maximum tokens for the result. If exceeded and the SQL
    /// contains an intent comment, results may be LLM-summarized.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns>Query results with column schema and rows.</returns>
    Task<QueryResult> ExecuteAsync(string sql, int tokenBudget, CancellationToken cancel = default);
}

/// <summary>
/// Transport-agnostic query result.
/// </summary>
public sealed class QueryResult
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public bool Truncated { get; init; }
    public string? Summary { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class QueryColumn
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
}
