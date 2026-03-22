using RepoQL.Contracts.Inference;

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
    Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancel = default);
}

/// <summary>
/// Abstraction over the database's raw SQL execution.
/// Implemented by the data layer (DuckDB), consumed by the query engine.
/// </summary>
public interface IQueryDataSource
{
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, CancellationToken cancel = default);
}

public sealed class QueryRequest
{
    public required string Sql { get; init; }
    public IReadOnlyList<QueryParameter>? Parameters { get; init; }
    public int TokenBudget { get; init; }
    public int Limit { get; init; }
}

public sealed class QueryParameter
{
    public QueryParameterKind Kind { get; init; }
    public string? StringValue { get; init; }
    public double? NumberValue { get; init; }
    public bool? BoolValue { get; init; }
}

public enum QueryParameterKind { Null, String, Number, Bool }

public sealed class QueryResult
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public int RowCount { get; init; }
    public bool Truncated { get; init; }
    public bool Summarized { get; init; }
    public int OriginalRowCount { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class QueryColumn
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
}
