namespace RepoQL.Formats.Python.Surface;

public sealed record PythonImportInfo(
    string? Module,
    IReadOnlyList<PythonImportName> Names,
    bool IsRelative,
    int RelativeLevel,
    bool IsStar,
    bool IsTypeCheckingOnly,
    PythonByteRange ByteRange);

public sealed record PythonImportName(
    string Name,
    string? Alias);
