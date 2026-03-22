namespace RepoQL.Formats.Python.Surface;

public sealed record PythonClassInfo(
    string Name,
    string QualifiedName,
    IReadOnlyList<string> BaseClasses,
    string? Metaclass,
    IReadOnlyList<PythonDecoratorInfo> Decorators,
    IReadOnlyList<PythonMethodInfo> Methods,
    IReadOnlyList<PythonVariableInfo> ClassVariables,
    IReadOnlyList<PythonVariableInfo> InstanceVariables,
    string? Slots,
    string? Docstring,
    PythonByteRange ByteRange);
