namespace RepoQL.ConsoleApp.Helpers;

internal readonly record struct QueryExecutionResult(
    string[] Lines,
    long TotalRowCount,
    long ExecutionTimeMs,
    int IndexPending,
    bool SemanticEnabled,
    bool SemanticReady);
