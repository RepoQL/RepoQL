namespace RepoQL.Formats.Rust.Surface;

public sealed record RustMacroDefInfo(
    string Name,
    string Visibility,
    RustByteRange ByteRange);