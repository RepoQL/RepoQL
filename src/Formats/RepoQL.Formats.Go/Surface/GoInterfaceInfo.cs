namespace RepoQL.Formats.Go.Surface;

public sealed record GoInterfaceInfo(
    string Name,
    bool IsExported,
    IReadOnlyList<GoInterfaceMethodInfo> Methods,
    IReadOnlyList<string> EmbeddedInterfaces,
    GoByteRange ByteRange);

