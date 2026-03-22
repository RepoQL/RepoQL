namespace RepoQL.Formats.Rust.Surface;

public sealed record RustAssociatedConstInfo(
    string Name,
    string? ConstType,
    bool HasDefault,
    RustByteRange ByteRange);