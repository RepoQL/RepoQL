namespace RepoQL.Formats.Rust.Surface;

public sealed record RustAttributeInfo(
    string Name,
    string? Arguments,
    RustByteRange ByteRange);