namespace RepoQL.Formats.Go.Surface;

public sealed record GoStructInfo(
    string Name,
    bool IsExported,
    IReadOnlyList<GoFieldInfo> Fields,
    GoByteRange ByteRange);
