namespace RepoQL.Formats.Go.Surface;

public sealed record GoFieldInfo(
    string Name,
    string TypeName,
    string? Tag,
    bool IsEmbedded,
    bool IsExported,
    GoByteRange ByteRange);

