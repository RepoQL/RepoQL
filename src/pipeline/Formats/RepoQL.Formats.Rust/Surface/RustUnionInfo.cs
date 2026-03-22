namespace RepoQL.Formats.Rust.Surface;

public sealed record RustUnionInfo(
    string Name,
    string QualifiedName,
    string Visibility,
    string? Generics,
    string? Derives,
    IReadOnlyList<RustAttributeInfo> Attributes,
    string? DocComment,
    RustByteRange ByteRange,
    IReadOnlyList<RustFieldInfo> Fields);
