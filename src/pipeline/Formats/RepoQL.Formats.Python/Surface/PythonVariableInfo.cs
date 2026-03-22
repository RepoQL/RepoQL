namespace RepoQL.Formats.Python.Surface;

public sealed record PythonVariableInfo(
    string Name,
    string? TypeAnnotation,
    PythonVariableKind VariableKind,
    PythonByteRange ByteRange);

public enum PythonVariableKind
{
    Instance,
    Class
}
