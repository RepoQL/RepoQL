namespace RepoQL.Formats.Rust.Surface;

public sealed record RustUseDeclarationInfo(
    string Path,
    string? Alias,
    bool IsGlob,
    bool IsPub,
    RustByteRange ByteRange);