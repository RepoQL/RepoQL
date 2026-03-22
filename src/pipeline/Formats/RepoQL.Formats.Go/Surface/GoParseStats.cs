namespace RepoQL.Formats.Go.Surface;

public sealed record GoParseStats(
    int StructCount,
    int InterfaceCount,
    int FunctionCount,
    int MethodCount,
    int ImportCount,
    int LineCount);

