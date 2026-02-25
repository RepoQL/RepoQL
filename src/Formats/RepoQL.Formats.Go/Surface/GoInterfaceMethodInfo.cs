namespace RepoQL.Formats.Go.Surface;

public sealed record GoInterfaceMethodInfo(
    string Name,
    string? Parameters,
    string? ReturnType,
    GoByteRange ByteRange);

