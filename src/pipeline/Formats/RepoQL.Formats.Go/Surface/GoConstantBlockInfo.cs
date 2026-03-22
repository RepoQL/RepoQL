namespace RepoQL.Formats.Go.Surface;

public sealed record GoConstantBlockInfo(
    IReadOnlyList<GoConstantInfo> Constants,
    string? TypeName,
    bool HasIota,
    GoByteRange ByteRange);

