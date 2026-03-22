namespace RepoQL.Formats.Rust.Surface;

public sealed record RustParseStats(
    int StructCount,
    int EnumCount,
    int TraitCount,
    int ImplCount,
    int FunctionCount,
    int LineCount);