namespace RepoQL.Formats.Rust.Surface;

public sealed record RustMethodInfo(
    string Name,
    string Visibility,
    bool IsAsync,
    bool IsUnsafe,
    bool IsConst,
    string SelfKind,
    string? Parameters,
    string? ReturnType,
    bool HasDefault,
    string? DocComment,
    RustByteRange ByteRange);