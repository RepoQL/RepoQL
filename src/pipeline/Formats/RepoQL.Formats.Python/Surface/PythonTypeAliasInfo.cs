namespace RepoQL.Formats.Python.Surface;

public sealed record PythonTypeAliasInfo(
    string Name,
    string? Definition,
    PythonByteRange ByteRange);
