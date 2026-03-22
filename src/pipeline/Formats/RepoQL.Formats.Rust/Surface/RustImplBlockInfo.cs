namespace RepoQL.Formats.Rust.Surface;

public sealed record RustImplBlockInfo(
    string TargetType,
    string? TraitName,
    string? Generics,
    string? WhereClause,
    bool IsUnsafe,
    RustByteRange ByteRange,
    IReadOnlyList<RustMethodInfo> Methods,
    IReadOnlyList<RustAssociatedTypeInfo> AssociatedTypes,
    IReadOnlyList<RustAssociatedConstInfo> AssociatedConsts);