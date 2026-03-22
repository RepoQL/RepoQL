namespace RepoQL.Formats.Rust.Surface;

public sealed record RustTypeAliasInfo(
    string Name,
    string QualifiedName,
    string Visibility,
    string? Generics,
    string? AliasedType,
    RustByteRange ByteRange);