namespace RepoQL.Formats.Rust.Surface;

public sealed record RustEnumInfo(
    string Name,
    string QualifiedName,
    string Visibility,
    string? Generics,
    string? WhereClause,
    string? Derives,
    IReadOnlyList<RustAttributeInfo> Attributes,
    string? DocComment,
    RustByteRange ByteRange,
    IReadOnlyList<RustEnumVariantInfo> Variants);