namespace RepoQL.Web.Services;

public sealed record SqlExecutionResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    long RowCount,
    bool Truncated);
