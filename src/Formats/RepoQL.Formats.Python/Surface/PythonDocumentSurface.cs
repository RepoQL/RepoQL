namespace RepoQL.Formats.Python.Surface;

public sealed record PythonDocumentSurface(
    IReadOnlyList<PythonClassInfo> Classes,
    IReadOnlyList<PythonFunctionInfo> Functions,
    IReadOnlyList<PythonImportInfo> Imports,
    IReadOnlyList<PythonConstantInfo> Constants,
    IReadOnlyList<PythonTypeAliasInfo> TypeAliases,
    string[]? AllExports,
    string? ModuleDocstring,
    IReadOnlyList<PythonMetaprogrammingHint> MetaprogrammingHints,
    IReadOnlyList<PythonFrameworkHint> FrameworkHints,
    PythonParseStats Stats,
    int ErrorNodeCount);
