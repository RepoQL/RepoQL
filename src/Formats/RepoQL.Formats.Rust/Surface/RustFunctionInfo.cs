namespace RepoQL.Formats.Rust.Surface;

public sealed record RustFunctionInfo(
    string Name,
    string QualifiedName,
    string Visibility,
    bool IsAsync,
    bool IsUnsafe,
    bool IsConst,
    string? Generics,
    string? Parameters,
    string? ReturnType,
    bool IsTest,
    string? DocComment,
    RustByteRange ByteRange);