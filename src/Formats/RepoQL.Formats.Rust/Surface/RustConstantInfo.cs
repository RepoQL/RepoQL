namespace RepoQL.Formats.Rust.Surface;

public sealed record RustConstantInfo(
    string Name,
    string Visibility,
    string? ConstType,
    string? DocComment,
    RustByteRange ByteRange);