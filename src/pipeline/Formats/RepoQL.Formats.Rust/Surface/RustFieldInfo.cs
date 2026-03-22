namespace RepoQL.Formats.Rust.Surface;

public sealed record RustFieldInfo(
    string Name,
    string Visibility,
    string? FieldType,
    string? DocComment,
    RustByteRange ByteRange);