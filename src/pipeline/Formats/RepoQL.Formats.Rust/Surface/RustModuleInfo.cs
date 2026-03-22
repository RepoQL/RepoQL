namespace RepoQL.Formats.Rust.Surface;

public sealed record RustModuleInfo(
    string Name,
    string QualifiedName,
    string Visibility,
    bool IsInline,
    string? DocComment,
    RustByteRange ByteRange);