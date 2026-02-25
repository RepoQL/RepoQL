namespace RepoQL.Formats.Rust.Surface;

public sealed record RustEnumVariantInfo(
    string Name,
    string VariantKind,
    IReadOnlyList<RustFieldInfo> Fields,
    string? Discriminant,
    string? DocComment,
    RustByteRange ByteRange);