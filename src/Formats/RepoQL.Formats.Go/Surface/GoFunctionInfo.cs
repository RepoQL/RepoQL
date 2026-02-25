namespace RepoQL.Formats.Go.Surface;

public sealed record GoFunctionInfo(
    string Name,
    bool IsExported,
    string? Parameters,
    string? ReturnType,
    GoByteRange ByteRange);

