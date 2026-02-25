namespace RepoQL.Formats.Rust.Surface;

public sealed record RustTraitInfo(
    string Name,
    string QualifiedName,
    string Visibility,
    string? Generics,
    string? WhereClause,
    string? Supertraits,
    bool IsAuto,
    bool IsUnsafe,
    string? DocComment,
    RustByteRange ByteRange,
    IReadOnlyList<RustMethodInfo> Methods,
    IReadOnlyList<RustAssociatedTypeInfo> AssociatedTypes,
    IReadOnlyList<RustAssociatedConstInfo> AssociatedConsts);