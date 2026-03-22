namespace RepoQL.Client.Helpers;

internal readonly record struct QueryExecutionResult(
    string[] Lines,
    long TotalRowCount,
    long ExecutionTimeMs,
    int IndexPending,
    int IndexTotal,
    int IndexFailed,
    int IndexStale,
    bool SemanticEnabled,
    bool SemanticReady,
    int SemanticPercent,
    bool Summarized = false,
    long OriginalRowCount = 0,
    bool SandboxError = false,
    string? RawJsOutput = null);
