namespace RepoQL.Formats.Rust.Surface;

public sealed record RustStaticInfo(
    string Name,
    string Visibility,
    string? StaticType,
    bool IsMutable,
    string? DocComment,
    RustByteRange ByteRange);