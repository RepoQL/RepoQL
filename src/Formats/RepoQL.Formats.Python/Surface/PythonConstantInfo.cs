namespace RepoQL.Formats.Python.Surface;

public sealed record PythonConstantInfo(
    string Name,
    string? TypeAnnotation,
    string? ValueText,
    bool IsFinal,
    bool IsAllCaps,
    PythonByteRange ByteRange);
