namespace RepoQL.Formats.Rust.Surface;

public sealed record RustQueryCapture(
    string Name,
    string Text,
    RustByteRange ByteRange);