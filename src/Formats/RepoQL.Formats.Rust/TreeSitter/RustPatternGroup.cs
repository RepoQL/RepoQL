namespace RepoQL.Formats.Rust.TreeSitter;

/// <summary>
/// Identifies which query group a <see cref="TreeSitter.QueryMatch.PatternIndex"/> belongs to
/// within <see cref="RustQueries.CombinedQuery"/>.
/// Used to dispatch matches from a single combined query execution into typed buckets.
/// </summary>
internal enum RustPatternGroup
{
    StructDeclarations,
    EnumDeclarations,
    TraitDeclarations,
    ImplBlocks,
    FunctionDeclarations,
    FunctionSignatures,
    ModuleDeclarations,
    UseDeclarations,
    Constants,
    Statics,
    TypeAliases,
    UnionDefinitions,
    MacroDefinitions,
    MacroInvocations,
    Attributes,
    VisibilityModifiers,
    ExternBlocks
}
