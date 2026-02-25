namespace RepoQL.Formats.Rust.Surface;

public sealed record RustExternBlockInfo(
    string? Abi,
    RustByteRange ByteRange,
    IReadOnlyList<RustExternFunctionInfo> Functions);