namespace RepoQL.Formats.Rust.Surface;

public sealed record RustExternFunctionInfo(
    string Name,
    string? Parameters,
    string? ReturnType,
    RustByteRange ByteRange);