namespace RepoQL.Formats.Rust.Surface;

public sealed record RustDocumentSurface(
    IReadOnlyList<RustStructInfo> Structs,
    IReadOnlyList<RustEnumInfo> Enums,
    IReadOnlyList<RustTraitInfo> Traits,
    IReadOnlyList<RustImplBlockInfo> ImplBlocks,
    IReadOnlyList<RustFunctionInfo> Functions,
    IReadOnlyList<RustModuleInfo> Modules,
    IReadOnlyList<RustConstantInfo> Constants,
    IReadOnlyList<RustStaticInfo> Statics,
    IReadOnlyList<RustTypeAliasInfo> TypeAliases,
    IReadOnlyList<RustUnionInfo> Unions,
    IReadOnlyList<RustMacroDefInfo> MacroDefs,
    IReadOnlyList<RustMacroInvocationInfo> MacroInvocations,
    IReadOnlyList<RustUseDeclarationInfo> UseDeclarations,
    IReadOnlyList<RustAttributeInfo> Attributes,
    IReadOnlyList<RustExternBlockInfo> ExternBlocks,
    RustParseStats Stats,
    int ErrorNodeCount);