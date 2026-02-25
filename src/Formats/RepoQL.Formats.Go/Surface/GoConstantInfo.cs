namespace RepoQL.Formats.Go.Surface;

public sealed record GoConstantInfo(
    string Name,
    string? TypeName,
    string? Value,
    bool IsExported,
    GoByteRange ByteRange);

