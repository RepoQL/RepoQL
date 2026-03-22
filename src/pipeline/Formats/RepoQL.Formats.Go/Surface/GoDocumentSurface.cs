namespace RepoQL.Formats.Go.Surface;

public sealed record GoDocumentSurface(
    string? PackageName,
    IReadOnlyList<GoImportInfo> Imports,
    IReadOnlyList<GoStructInfo> Structs,
    IReadOnlyList<GoInterfaceInfo> Interfaces,
    IReadOnlyList<GoTypeDefinitionInfo> TypeDefinitions,
    IReadOnlyList<GoConstantInfo> Constants,
    IReadOnlyList<GoConstantBlockInfo> ConstantBlocks,
    IReadOnlyList<GoVariableInfo> Variables,
    IReadOnlyList<GoDirectiveInfo> Directives,
    IReadOnlyList<GoFunctionInfo> Functions,
    IReadOnlyList<GoFunctionInfo> InitFunctions,
    IReadOnlyList<GoMethodInfo> Methods,
    GoParseStats Stats,
    int ErrorNodeCount);
