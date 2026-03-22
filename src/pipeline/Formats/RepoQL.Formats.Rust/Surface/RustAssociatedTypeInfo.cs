namespace RepoQL.Formats.Rust.Surface;

public sealed record RustAssociatedTypeInfo(
    string Name,
    string? Bounds,
    string? DefaultType,
    RustByteRange ByteRange);