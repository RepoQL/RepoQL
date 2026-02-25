namespace RepoQL.Formats.Python.Surface;

public sealed record PythonParseStats(
    int ClassCount,
    int FunctionCount,
    int ImportCount,
    int LineCount,
    int ErrorNodeCount);
